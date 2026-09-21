using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using O2P.Infrastructure.Oracle;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.Reader
{
    public class OracleDataReader : IOracleDataReader
    {
        public async Task ReadChunkDataAsync(Connection connection, string password, string owner, string tableName, IReadOnlyList<ManifestColumn> columns, string? whereClause, ChunkLog chunk, ChannelWriter<object[]> outputChannel, IRateLimiter? rateLimiter, CancellationToken cancellationToken)
        {
            if (connection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase))
            {
                // Emulate Oracle data loading
                for (int i = 0; i < 500; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    await Task.Delay(5, cancellationToken); // Simulates latency

                    if (rateLimiter != null)
                    {
                        await rateLimiter.WaitAsync(1, cancellationToken);
                    }

                    var mockRow = columns.Select((column, index) => MockValue(column, i, index, tableName, chunk.ChunkIndex)).ToArray();

                    await outputChannel.WriteAsync(mockRow, cancellationToken);
                }
                outputChannel.Complete();
                return;
            }

            var csb = new OracleConnectionStringBuilder
            {
                DataSource = $"{connection.Host}:{connection.Port}/{connection.ServiceOrDb}",
                UserID = connection.Username,
                Password = password,
                Pooling = true,
                MinPoolSize = 1,
                MaxPoolSize = 100,
                // Extra headroom for a high-latency / constrained source: wait longer for a pooled
                // session before surfacing ORA-50000 rather than failing the chunk immediately.
                ConnectionTimeout = 60
            };

            using var conn = new OracleConnection(csb.ConnectionString);
            await OracleConnectionRetry.OpenWithRetryAsync(conn, cancellationToken);

            var includedColumns = columns.Where(c => !c.IsExcluded).ToList();
            var columnList = string.Join(", ", includedColumns.Select(c => SqlIdentifier.QuoteOracle(c.ColumnName)));

            // Confine partition/partition_rowid chunks to their partition so the optimizer prunes to it.
            var fromClause = SqlIdentifier.OracleQualified(owner, tableName);
            if (!string.IsNullOrEmpty(chunk.PartitionName))
            {
                fromClause += $" PARTITION ({SqlIdentifier.QuoteOracle(chunk.PartitionName)})";
            }

            string sql = $"SELECT {columnList} FROM {fromClause}";
            var predicates = new List<string>();

            if (!string.IsNullOrWhiteSpace(whereClause))
            {
                predicates.Add($"({OracleValues.ValidateReadOnlyWhereClause(whereClause)})");
            }

            // Slice the chunk according to its strategy. pk_range carries numeric key bounds on
            // BoundColumn; the rowid strategies carry physical ROWID bounds. "single"/"partition"
            // read the whole (partition-scoped) set with no extra range predicate.
            var isPkRange = chunk.Strategy == "pk_range" && !string.IsNullOrEmpty(chunk.BoundColumn);
            var isRowidHash = chunk.Strategy == "rowid_hash";
            if (isPkRange)
            {
                var boundCol = SqlIdentifier.QuoteOracle(chunk.BoundColumn!);
                if (chunk.StartRowId != "MIN") predicates.Add($"{boundCol} > :startKey");
                if (chunk.EndRowId != "MAX") predicates.Add($"{boundCol} <= :endKey");
            }
            else if (isRowidHash)
            {
                // StartRowId = bucket index, EndRowId = total buckets. ORA_HASH(expr, max_bucket)
                // returns 0..max_bucket, so hashing ROWID assigns each row to exactly one bucket.
                var bucket = int.Parse(chunk.StartRowId, CultureInfo.InvariantCulture);
                var total = int.Parse(chunk.EndRowId, CultureInfo.InvariantCulture);
                if (total > 1)
                {
                    predicates.Add($"ORA_HASH(ROWID, {total - 1}) = {bucket}");
                }
            }
            else if (chunk.StartRowId != "MIN" && chunk.EndRowId != "MAX")
            {
                predicates.Add("ROWID BETWEEN :startRowId AND :endRowId");
            }

            if (predicates.Count > 0)
            {
                sql += " WHERE " + string.Join(" AND ", predicates);
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.BindByName = true;
            // Bound Oracle command time so a blocked session cannot hold a worker slot forever.
            // CancelAfter on the worker CTS is the outer guard; this helps ODP.NET abort sooner.
            cmd.CommandTimeout = 600;
            if (isPkRange)
            {
                if (chunk.StartRowId != "MIN")
                    cmd.Parameters.Add(new OracleParameter("startKey", OracleDbType.Decimal) { Value = decimal.Parse(chunk.StartRowId, CultureInfo.InvariantCulture) });
                if (chunk.EndRowId != "MAX")
                    cmd.Parameters.Add(new OracleParameter("endKey", OracleDbType.Decimal) { Value = decimal.Parse(chunk.EndRowId, CultureInfo.InvariantCulture) });
            }
            else if (!isRowidHash && chunk.StartRowId != "MIN" && chunk.EndRowId != "MAX")
            {
                cmd.Parameters.Add(new OracleParameter("startRowId", chunk.StartRowId));
                cmd.Parameters.Add(new OracleParameter("endRowId", chunk.EndRowId));
            }
            // Fetch tuning. A 16 MB row-prefetch buffer gives great throughput for narrow rows, but is
            // pathological for BLOB/CLOB columns: ODP.NET tries to fill 16 MB with LOB data before
            // returning the first row, so on a high-latency source the read produces no rows and the
            // chunk just re-leases forever ("stuck"). For LOB tables use a small prefetch and cap the
            // inline LOB fetch so large LOBs stream in instead of being buffered whole up front.
            var hasLob = includedColumns.Any(c => OracleValues.IsLob(c.OracleDataType));
            if (hasLob)
            {
                cmd.FetchSize = 1 * 1024 * 1024;   // 1 MB prefetch so the first rows return promptly
                cmd.InitialLOBFetchSize = 65536;    // 64 KB of each LOB inline; stream the remainder
            }
            else
            {
                cmd.FetchSize = 16 * 1024 * 1024;   // high throughput for narrow rows
            }

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            int fieldCount = reader.FieldCount;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (rateLimiter != null)
                {
                    await rateLimiter.WaitAsync(1, cancellationToken);
                }

                var values = new object[fieldCount];
                reader.GetValues(values);
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = OracleValues.Normalize(values[i]);
                }
                await outputChannel.WriteAsync(values, cancellationToken);
            }

            outputChannel.Complete();
        }

        private static object? MockValue(ManifestColumn column, int row, int index, string tableName, int chunkIndex)
        {
            var type = column.PostgresDataType.ToLowerInvariant();
            if (type.Contains("bigint")) return (long)(row + (chunkIndex * 1000));
            if (type.Contains("integer") || type.Contains("smallint")) return row + (chunkIndex * 1000);
            if (type.Contains("timestamp")) return DateTime.UtcNow.AddMinutes(-row);
            if (type.Contains("bytea")) return Array.Empty<byte>();
            if (type.Contains("numeric")) return Convert.ToDecimal(row);
            return $"{tableName}_{column.ColumnName}_{row}";
        }
    }
}
