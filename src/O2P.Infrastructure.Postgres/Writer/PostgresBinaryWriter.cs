using Npgsql;
using NpgsqlTypes;
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
        public async Task<long> WriteDataAsync(Connection connection, string password, string targetSchema, string targetTable, IReadOnlyList<ManifestColumn> columns, long jobRunId, long tableRunId, int chunkIndex, ChannelReader<object[]> inputChannel, CancellationToken cancellationToken)
        {
            if (connection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase))
            {
                long rows = 0;
                await foreach (var row in inputChannel.ReadAllAsync(cancellationToken))
                {
                    rows++;
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
                CommandTimeout = 600
            };

            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            var qualifiedTable = SqlIdentifier.QuotePostgresQualified(targetSchema, targetTable);
            var includedColumns = columns.Where(c => !c.IsExcluded).OrderBy(c => c.Id).ToList();
            var copyColumns = string.Join(", ", includedColumns.Select(c => SqlIdentifier.QuotePostgres(c.ColumnName)));

            // Precompute the binary write plan per column. Oracle NUMBER/FLOAT come back from ODP.NET
            // as .NET decimal regardless of the target column type, so a decimal written with type
            // inference produces `numeric` binary format and Postgres rejects it against an
            // integer/real/double column ("22P03: incorrect binary data format"). For those columns we
            // write with the target column's explicit NpgsqlDbType and coerce the CLR value to match.
            var columnPlans = includedColumns.Select(c => ResolveWritePlan(c.PostgresDataType)).ToArray();

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
                    await importer.StartRowAsync(cancellationToken);
                    for (var i = 0; i < row.Length; i++)
                    {
                        var val = row[i];
                        if (val == System.DBNull.Value || val == null)
                        {
                            await importer.WriteNullAsync(cancellationToken);
                            continue;
                        }

                        var plan = i < columnPlans.Length ? columnPlans[i] : default;
                        if (plan.Typed)
                        {
                            await importer.WriteAsync(plan.Coerce(val), plan.DbType, cancellationToken);
                        }
                        else
                        {
                            // Non-numeric targets (text, timestamp, bytea, bool, ...) match their CLR
                            // type, so type inference is correct and avoids over-constraining edge types.
                            await importer.WriteAsync(val, cancellationToken);
                        }
                    }
                    rowsWritten++;
                }

                await importer.CompleteAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return rowsWritten;
        }

        private readonly record struct WritePlan(bool Typed, NpgsqlDbType DbType, Func<object, object> Coerce);

        // Maps a target Postgres column type to how its value must be written in a binary COPY.
        // Only the numeric types need an explicit NpgsqlDbType + coercion (because Oracle numbers all
        // arrive as .NET decimal); everything else is written via CLR-type inference, which is correct.
        private static WritePlan ResolveWritePlan(string postgresType)
        {
            var baseType = Regex.Replace((postgresType ?? string.Empty).Trim().ToLowerInvariant(), @"\(.*?\)", "").Trim();
            return baseType switch
            {
                "smallint" => new WritePlan(true, NpgsqlDbType.Smallint, v => Convert.ToInt16(v, CultureInfo.InvariantCulture)),
                "integer" => new WritePlan(true, NpgsqlDbType.Integer, v => Convert.ToInt32(v, CultureInfo.InvariantCulture)),
                "bigint" => new WritePlan(true, NpgsqlDbType.Bigint, v => Convert.ToInt64(v, CultureInfo.InvariantCulture)),
                "real" => new WritePlan(true, NpgsqlDbType.Real, v => Convert.ToSingle(v, CultureInfo.InvariantCulture)),
                "double precision" => new WritePlan(true, NpgsqlDbType.Double, v => Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                "numeric" => new WritePlan(true, NpgsqlDbType.Numeric, v => Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
                _ => new WritePlan(false, default, v => v),
            };
        }

        private static async Task DrainAsync(ChannelReader<object[]> inputChannel, CancellationToken cancellationToken)
        {
            await foreach (var _ in inputChannel.ReadAllAsync(cancellationToken))
            {
            }
        }
    }
}
