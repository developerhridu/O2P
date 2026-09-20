using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.Discovery
{
    public class OracleDiscoveryService : IOracleDiscoveryService
    {
        public async Task<IEnumerable<DiscoveryCache>> DiscoverTablesAsync(Connection connection, string password, string owner, CancellationToken cancellationToken, IReadOnlyCollection<string>? tableNames = null)
        {
            var normalizedTableNames = tableNames == null || tableNames.Count == 0
                ? null
                : tableNames.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0).Distinct().ToList();

            if (connection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                var mockTables = new List<DiscoveryCache>
                {
                    new DiscoveryCache
                    {
                        ConnectionId = connection.Id,
                        Owner = owner.ToUpperInvariant(),
                        TableName = "CUSTOMERS",
                        NumRows = 1500,
                        SegmentBytes = 256 * 1024,
                        IsPartitioned = false,
                        IsIot = false,
                        LastRefreshedAt = DateTimeOffset.UtcNow
                    },
                    new DiscoveryCache
                    {
                        ConnectionId = connection.Id,
                        Owner = owner.ToUpperInvariant(),
                        TableName = "ORDERS",
                        NumRows = 4500,
                        SegmentBytes = 512 * 1024,
                        IsPartitioned = true,
                        IsIot = false,
                        LastRefreshedAt = DateTimeOffset.UtcNow
                    }
                };

                mockTables[0].Columns.Add(new DiscoveryColumnCache { ColumnName = "ID", ColumnId = 1, DataType = "NUMBER", DataPrecision = 12, DataScale = 0, IsNullable = false });
                mockTables[0].Columns.Add(new DiscoveryColumnCache { ColumnName = "NAME", ColumnId = 2, DataType = "VARCHAR2", DataLength = 100, IsNullable = false });
                mockTables[0].Columns.Add(new DiscoveryColumnCache { ColumnName = "EMAIL", ColumnId = 3, DataType = "VARCHAR2", DataLength = 150, IsNullable = true });
                mockTables[0].Columns.Add(new DiscoveryColumnCache { ColumnName = "CREATED_AT", ColumnId = 4, DataType = "DATE", IsNullable = false });

                mockTables[1].Columns.Add(new DiscoveryColumnCache { ColumnName = "ID", ColumnId = 1, DataType = "NUMBER", DataPrecision = 12, DataScale = 0, IsNullable = false });
                mockTables[1].Columns.Add(new DiscoveryColumnCache { ColumnName = "CUSTOMER_ID", ColumnId = 2, DataType = "NUMBER", DataPrecision = 12, DataScale = 0, IsNullable = false });
                mockTables[1].Columns.Add(new DiscoveryColumnCache { ColumnName = "TOTAL_AMOUNT", ColumnId = 3, DataType = "NUMBER", DataPrecision = 10, DataScale = 2, IsNullable = false });
                mockTables[1].Columns.Add(new DiscoveryColumnCache { ColumnName = "STATUS", ColumnId = 4, DataType = "VARCHAR2", DataLength = 20, IsNullable = false });
                mockTables[1].Columns.Add(new DiscoveryColumnCache { ColumnName = "CREATED_AT", ColumnId = 5, DataType = "DATE", IsNullable = false });

                if (normalizedTableNames != null)
                {
                    return mockTables.Where(t => normalizedTableNames.Contains(t.TableName)).ToList();
                }

                return mockTables;
            }

            var csb = BuildConnectionString(connection, password);

            var results = new List<DiscoveryCache>();

            using var conn = new OracleConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            // Enriched query adds segment/LOB sizes via ALL_SEGMENTS/ALL_LOBS. Those views are
            // locked down on some hardened Oracle deploys (ORA-00942). Probe both views first so
            // we never run the enriched query when it would throw (avoids VS first-chance noise).
            string enrichedQuery = @"
                WITH SegStats AS (
                    SELECT segment_name, SUM(bytes) as segment_bytes
                    FROM all_segments
                    WHERE owner = :owner AND segment_type LIKE 'TABLE%'
                    GROUP BY segment_name
                ),
                LobStats AS (
                    SELECT l.table_name, SUM(s.bytes) as lob_bytes
                    FROM all_lobs l
                    JOIN all_segments s ON s.owner = l.owner AND s.segment_name = l.segment_name
                    WHERE l.owner = :owner
                    GROUP BY l.table_name
                )
                SELECT
                    t.table_name,
                    t.num_rows,
                    s.segment_bytes,
                    l.lob_bytes,
                    t.partitioned,
                    t.iot_type
                FROM all_tables t
                LEFT JOIN SegStats s ON t.table_name = s.segment_name
                LEFT JOIN LobStats l ON t.table_name = l.table_name
                WHERE t.owner = :owner
                AND t.nested = 'NO'
                AND (t.iot_type IS NULL OR t.iot_type = 'IOT')"; // Exclude IOT overflow

            string fallbackQuery = @"
                SELECT
                    t.table_name,
                    t.num_rows,
                    NULL as segment_bytes,
                    NULL as lob_bytes,
                    t.partitioned,
                    t.iot_type
                FROM all_tables t
                WHERE t.owner = :owner
                AND t.nested = 'NO'
                AND (t.iot_type IS NULL OR t.iot_type = 'IOT')";

            if (normalizedTableNames != null)
            {
                var placeholders = string.Join(", ", normalizedTableNames.Select((_, i) => $":tn{i}"));
                enrichedQuery += $" AND t.table_name IN ({placeholders})";
                fallbackQuery += $" AND t.table_name IN ({placeholders})";
            }

            var useEnriched = await CanQueryAsync(conn, "SELECT 1 FROM all_segments WHERE 1 = 0", cancellationToken)
                && await CanQueryAsync(conn, "SELECT 1 FROM all_lobs WHERE 1 = 0", cancellationToken);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = useEnriched ? enrichedQuery : fallbackQuery;
            AddTableQueryParameters(cmd, owner, normalizedTableNames);
            var reader = (OracleDataReader)await cmd.ExecuteReaderAsync(cancellationToken);

            using (reader)
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var cache = new DiscoveryCache
                    {
                        ConnectionId = connection.Id,
                        Owner = owner.ToUpperInvariant(),
                        TableName = reader.GetString(0),
                        NumRows = reader.IsDBNull(1) ? null : (long?)reader.GetDecimal(1),
                        SegmentBytes = reader.IsDBNull(2) ? null : (long?)reader.GetDecimal(2),
                        LobBytes = reader.IsDBNull(3) ? null : (long?)reader.GetDecimal(3),
                        IsPartitioned = !reader.IsDBNull(4) && reader.GetString(4) == "YES",
                        IsIot = !reader.IsDBNull(5),
                        LastRefreshedAt = DateTimeOffset.UtcNow
                    };
                    results.Add(cache);
                }
            }

            // M3 Column Discovery
            string colQuery = @"
                SELECT table_name, column_name, column_id, data_type, data_length, data_precision, data_scale, nullable, identity_column
                FROM all_tab_columns
                WHERE owner = :owner";

            if (normalizedTableNames != null)
            {
                var colPlaceholders = string.Join(", ", normalizedTableNames.Select((_, i) => $":tn{i}"));
                colQuery += $" AND table_name IN ({colPlaceholders})";
            }
            colQuery += " ORDER BY table_name, column_id";

            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = colQuery;
            var colOwnerParam = colCmd.CreateParameter();
            colOwnerParam.ParameterName = "owner";
            colOwnerParam.Value = owner.ToUpperInvariant();
            colCmd.Parameters.Add(colOwnerParam);

            if (normalizedTableNames != null)
            {
                for (var i = 0; i < normalizedTableNames.Count; i++)
                {
                    var colTnParam = colCmd.CreateParameter();
                    colTnParam.ParameterName = $"tn{i}";
                    colTnParam.Value = normalizedTableNames[i];
                    colCmd.Parameters.Add(colTnParam);
                }
            }

            var columnsByTable = new Dictionary<string, List<DiscoveryColumnCache>>();
            using var colReader = await colCmd.ExecuteReaderAsync(cancellationToken);
            while (await colReader.ReadAsync(cancellationToken))
            {
                var tName = colReader.GetString(0);
                var col = new DiscoveryColumnCache
                {
                    ColumnName = colReader.GetString(1),
                    ColumnId = (int)colReader.GetDecimal(2),
                    DataType = colReader.GetString(3),
                    DataLength = colReader.IsDBNull(4) ? null : (int?)colReader.GetDecimal(4),
                    DataPrecision = colReader.IsDBNull(5) ? null : (int?)colReader.GetDecimal(5),
                    DataScale = colReader.IsDBNull(6) ? null : (int?)colReader.GetDecimal(6),
                    IsNullable = colReader.GetString(7) == "Y",
                    IsIdentity = !colReader.IsDBNull(8) && colReader.GetString(8) == "YES"
                };

                if (!columnsByTable.ContainsKey(tName))
                {
                    columnsByTable[tName] = new List<DiscoveryColumnCache>();
                }
                columnsByTable[tName].Add(col);
            }

            foreach (var r in results)
            {
                if (columnsByTable.TryGetValue(r.TableName, out var cols))
                {
                    foreach (var c in cols)
                    {
                        r.Columns.Add(c);
                    }
                }
            }

            return results;
        }

        // Oracle's built-in schemas, for databases old enough to lack ALL_USERS.ORACLE_MAINTAINED
        // (added in 12.1). Only used as a fallback; the column is authoritative where it exists.
        private static readonly string[] KnownSystemSchemas =
        {
            "SYS", "SYSTEM", "XDB", "CTXSYS", "MDSYS", "ORDSYS", "ORDDATA", "ORDPLUGINS", "SI_INFORMTN_SCHEMA",
            "OLAPSYS", "WMSYS", "EXFSYS", "DBSNMP", "OUTLN", "APPQOSSYS", "DIP", "ANONYMOUS", "XS$NULL",
            "LBACSYS", "DVSYS", "DVF", "AUDSYS", "GSMADMIN_INTERNAL", "ORACLE_OCM", "SYSMAN", "MDDATA",
            "FLOWS_FILES", "APEX_PUBLIC_USER", "OJVMSYS", "REMOTE_SCHEDULER_AGENT", "SYSBACKUP", "SYSDG",
            "SYSKM", "SYSRAC", "GGSYS", "DBSFWUSER"
        };

        // The server upper-cases every owner it is given, so only names that are already upper-case
        // and are safe to quote (see SqlIdentifier.QuoteOracle) can actually be scanned.
        private static readonly Regex ScannableSchemaName = new(@"^[A-Z_][A-Z0-9_$#]{0,127}$", RegexOptions.Compiled);

        public async Task<SourceSchemaList> ListSchemasAsync(Connection connection, string password, CancellationToken cancellationToken)
        {
            if (connection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                // APP matches the mock scan (two tables) so the whole flow is testable without Oracle.
                return new SourceSchemaList(
                    new[] { new SourceSchema("APP", 2), new SourceSchema("HR", 7), new SourceSchema("SALES", 12) },
                    0);
            }

            var csb = BuildConnectionString(connection, password);
            using var conn = new OracleConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            // ORACLE_MAINTAINED needs 12.1+; on 11g the column does not exist (ORA-00904). Probe first,
            // the same way discovery probes ALL_SEGMENTS/ALL_LOBS, rather than running a query that throws.
            var hasOracleMaintained = await CanQueryAsync(conn, "SELECT oracle_maintained FROM all_users WHERE 1 = 0", cancellationToken);
            var systemFilter = hasOracleMaintained
                ? "AND t.owner IN (SELECT username FROM all_users WHERE oracle_maintained = 'N')"
                : $"AND t.owner NOT IN ({string.Join(", ", KnownSystemSchemas.Select(s => $"'{s}'"))})";

            // Same table filter as DiscoverTablesAsync (nested = 'NO', the IOT rule) so the count beside
            // a schema is what a scan of it will find. ALL_* views only: an ordinary source account has
            // no DBA_* access.
            var sql = $@"
SELECT t.owner, COUNT(*) AS table_count
FROM all_tables t
WHERE t.nested = 'NO'
  AND (t.iot_type IS NULL OR t.iot_type = 'IOT')
  {systemFilter}
GROUP BY t.owner
ORDER BY t.owner";

            var schemas = new List<SourceSchema>();
            var skipped = 0;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var count = Convert.ToInt32(reader.GetValue(1));

                if (!ScannableSchemaName.IsMatch(name))
                {
                    skipped++;
                    continue;
                }

                schemas.Add(new SourceSchema(name, count));
            }

            return new SourceSchemaList(schemas, skipped);
        }

        private static OracleConnectionStringBuilder BuildConnectionString(Connection connection, string password) => new()
        {
            DataSource = $"{connection.Host}:{connection.Port}/{connection.ServiceOrDb}",
            UserID = connection.Username,
            Password = password,
            Pooling = true,
            MinPoolSize = 1,
            MaxPoolSize = 10
        };

        private static async Task<bool> CanQueryAsync(OracleConnection conn, string sql, CancellationToken cancellationToken)
        {
            try
            {
                using var probe = conn.CreateCommand();
                probe.CommandText = sql;
                await probe.ExecuteScalarAsync(cancellationToken);
                return true;
            }
            catch (OracleException)
            {
                return false;
            }
        }

        private static void AddTableQueryParameters(System.Data.Common.DbCommand cmd, string owner, List<string>? normalizedTableNames)
        {
            var ownerParam = cmd.CreateParameter();
            ownerParam.ParameterName = "owner";
            ownerParam.Value = owner.ToUpperInvariant();
            cmd.Parameters.Add(ownerParam);

            if (normalizedTableNames != null)
            {
                for (var i = 0; i < normalizedTableNames.Count; i++)
                {
                    var tnParam = cmd.CreateParameter();
                    tnParam.ParameterName = $"tn{i}";
                    tnParam.Value = normalizedTableNames[i];
                    cmd.Parameters.Add(tnParam);
                }
            }
        }
    }
}
