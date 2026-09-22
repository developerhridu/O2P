using Npgsql;
using NpgsqlTypes;
using O2P.Application.Copying;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Writer
{
    public class PostgresBinaryWriter : IPostgresBinaryWriter
    {
        public async Task<long> WriteDataAsync(Connection connection, string password, string targetSchema, string targetTable, IReadOnlyList<ManifestColumn> columns, string? targetNameStyle, long jobRunId, long tableRunId, int chunkIndex, ChannelReader<object[]> inputChannel, ChunkProgress? progress, CancellationToken cancellationToken)
        {
            if (connection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase))
            {
                long rows = 0;
                await foreach (var row in inputChannel.ReadAllAsync(cancellationToken))
                {
                    rows++;
                    progress?.RowWritten(RowBytes(row));
                }
                return rows;
            }

            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = connection.Host,
                Port = connection.Port,
                Database = connection.ServiceOrDb,
                Username = connection.Username,
                Password = password,
                Pooling = true,
                MinPoolSize = 1,
                MaxPoolSize = 100,
                Timeout = 60,
                CommandTimeout = 600,
                // Keepalive every 30 s so a VPN or firewall does not silently drop a connection that sits
                // idle while the reader waits on Oracle.
                KeepAlive = 30
            };

            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            var qualifiedTable = SqlIdentifier.QuotePostgresQualified(targetSchema, targetTable);
            // Must stay the same projection, in the same order, as the Oracle SELECT the reader built:
            // the rows arriving on the channel are positional, field i goes to copyColumns[i]. Only the
            // spelling of each name differs between the two sides.
            var includedColumns = columns.Where(c => !c.IsExcluded).OrderBy(c => c.Id).ToList();
            var copyColumns = string.Join(", ", includedColumns.Select(c => SqlIdentifier.QuotePostgres(PostgresName.TargetColumn(c, targetNameStyle))));

            // Precompute the binary write plan per column; see PostgresWritePlan for why numbers need one.
            var columnPlans = PostgresWritePlan.ResolveAll(includedColumns.Select(c => c.PostgresDataType));

            await using var tx = await conn.BeginTransactionAsync(cancellationToken);

            await using (var fenceCmd = conn.CreateCommand())
            {
                fenceCmd.Transaction = tx;
                fenceCmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {SqlIdentifier.QuotePostgresQualified(targetSchema, "_o2p_chunk_log")} (
    job_run_id bigint NOT NULL,
    table_run_id bigint NOT NULL,
    chunk_index integer NOT NULL,
    completed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (job_run_id, table_run_id, chunk_index)
);
INSERT INTO {SqlIdentifier.QuotePostgresQualified(targetSchema, "_o2p_chunk_log")} (job_run_id, table_run_id, chunk_index)
VALUES (@job_run_id, @table_run_id, @chunk_index)
ON CONFLICT DO NOTHING
RETURNING chunk_index;";
                fenceCmd.Parameters.AddWithValue("job_run_id", jobRunId);
                fenceCmd.Parameters.AddWithValue("table_run_id", tableRunId);
                fenceCmd.Parameters.AddWithValue("chunk_index", chunkIndex);

                var inserted = await fenceCmd.ExecuteScalarAsync(cancellationToken);
                if (inserted == null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    await DrainAsync(inputChannel, cancellationToken);
                    return 0;
                }
            }

            long rowsWritten = 0;

            // A binary importer holds the connection in COPY state until it is DISPOSED, not merely
            // Completed. Committing the transaction while the importer is still alive throws
            // "connection is already in state 'Copy'". So run the COPY inside its own await-using
            // scope: Complete then dispose the importer (async teardown sends the proper COPY
            // done/fail and returns the connection to a clean state), and only then commit.
            await using (var importer = await conn.BeginBinaryImportAsync($"COPY {qualifiedTable} ({copyColumns}) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                await foreach (var row in inputChannel.ReadAllAsync(cancellationToken))
                {
                    await PostgresWritePlan.WriteRowAsync(importer, row, columnPlans, cancellationToken);
                    rowsWritten++;
                    progress?.RowWritten(RowBytes(row));
                }

                await importer.CompleteAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return rowsWritten;
        }

        private static long RowBytes(object[] row)
        {
            long bytes = 0;
            foreach (var value in row) bytes += ChunkProgress.SizeOf(value);
            return bytes;
        }

        private static async Task DrainAsync(ChannelReader<object[]> inputChannel, CancellationToken cancellationToken)
        {
            await foreach (var _ in inputChannel.ReadAllAsync(cancellationToken))
            {
            }
        }
    }
}
