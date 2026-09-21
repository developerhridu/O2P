using Microsoft.Extensions.Logging;
using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    /// <summary>
    /// The Oracle side of change tracking: where a copy starts, which key identifies a row, and whether
    /// the redo log will carry what is needed.
    /// </summary>
    public partial class OracleChangeSource : IOracleChangeSource
    {
        /// <summary>
        /// Key types whose values can be taken as text from the redo and bound back exactly. Anything else
        /// (LOBs, user types, ROWID keys, floating binary types whose text form is lossy) makes the table
        /// untrackable rather than risk matching the wrong row.
        /// </summary>
        private static readonly HashSet<string> SupportedKeyTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "NUMBER", "INTEGER", "FLOAT", "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "DATE", "RAW"
        };

        private readonly ILogger<OracleChangeSource> _logger;

        public OracleChangeSource(ILogger<OracleChangeSource> logger)
        {
            _logger = logger;
        }

        public async Task<SourceStartPoint> CaptureStartPointAsync(Connection connection, string password, string owner, string tableName, CancellationToken cancellationToken)
        {
            if (OracleSessions.IsMock(connection))
            {
                // The mock source has no redo, but the screens and state changes around tracking still
                // need something to run against. Its tables are keyed on ID.
                return new SourceStartPoint(1000m, true, new[] { new OracleKeyColumn("ID", "NUMBER") }, null, "{\"mock\":true}", Array.Empty<string>());
            }

            var problems = new List<string>();
            decimal? startScn = null;
            var loggingReady = false;
            IReadOnlyList<OracleKeyColumn>? key = null;
            string? keyProblem = null;
            string? objectIds = null;

            try
            {
                // Non-pooled: nothing here must share a session with the bulk reader's pool.
                await using var conn = await OracleSessions.OpenUnpooledAsync(connection, password, cancellationToken);

                // The order matters. S0 first, then the open transactions: a transaction already open
                // when they are read holds the start point back to its own start; one that opens later
                // starts after S0. Either way its changes are at or after the start point, so the first
                // "Copy changes" looks at them again - whether or not the fuzzy load happened to see them.
                try
                {
                    var s0 = await ScalarDecimalAsync(conn, "SELECT CURRENT_SCN FROM V$DATABASE", cancellationToken);
                    var oldestOpen = await ScalarDecimalAsync(conn, "SELECT MIN(START_SCN) FROM GV$TRANSACTION", cancellationToken);
                    if (s0 != null)
                    {
                        startScn = oldestOpen == null ? s0 - 1 : Math.Min(s0.Value - 1, oldestOpen.Value - 1);
                    }
                }
                catch (OracleException ex)
                {
                    // Not guessing: a start point that ignores open transactions can lose an update.
                    problems.Add($"Could not read Oracle's current position in its change history ({ex.Number}). {Grants.StartPoint}");
                }

                loggingReady = await IsLoggingReadyAsync(conn, owner, tableName, problems, cancellationToken);
                (key, keyProblem) = await ReadKeyAsync(conn, owner, tableName, cancellationToken);
                if (keyProblem != null) problems.Add(keyProblem);
                objectIds = await ReadObjectIdsAsync(conn, owner, tableName, problems, cancellationToken);
            }
            catch (Exception ex) when (ex is OracleException || ex is InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not record a change-tracking start point for {Owner}.{Table}", owner, tableName);
                problems.Add($"Could not record where this copy started: {ex.Message}");
            }

            return new SourceStartPoint(startScn, loggingReady, key, keyProblem, objectIds, problems);
        }

        private static async Task<bool> IsLoggingReadyAsync(OracleConnection conn, string owner, string tableName, List<string> problems, CancellationToken ct)
        {
            try
            {
                string logMode, minimal, pk;
                await using (var cmd = new OracleCommand("SELECT LOG_MODE, SUPPLEMENTAL_LOG_DATA_MIN, SUPPLEMENTAL_LOG_DATA_PK FROM V$DATABASE", conn))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct)) return false;
                    logMode = reader.GetString(0);
                    minimal = reader.GetString(1);
                    pk = reader.GetString(2);
                }

                var ready = true;
                if (!string.Equals(logMode, "ARCHIVELOG", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("The source database is not in ARCHIVELOG mode, so its change history is lost as the online logs are reused.");
                    ready = false;
                }

                // IMPLICIT counts: Oracle switches minimal logging on by itself once PK logging is on.
                if (!(minimal.Equals("YES", StringComparison.OrdinalIgnoreCase) || minimal.Equals("IMPLICIT", StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add("Minimal supplemental logging is off, so changes to chained or migrated rows would be incomplete.");
                    ready = false;
                }

                if (!pk.Equals("YES", StringComparison.OrdinalIgnoreCase)
                    && !await HasPdbPkLoggingAsync(conn, ct)
                    && !await HasTablePkLoggingAsync(conn, owner, tableName, ct))
                {
                    problems.Add($"Primary-key supplemental logging is off for {owner}.{tableName}, so an update that does not change the key would not say which row it changed.");
                    ready = false;
                }

                return ready;
            }
            catch (OracleException ex)
            {
                problems.Add($"Could not read the source's logging settings ({ex.Number}). {Grants.StartPoint}");
                return false;
            }
        }

        /// <summary>PDB-level supplemental logging (12.2+) is not visible in V$DATABASE. Absent view = not on.</summary>
        private static async Task<bool> HasPdbPkLoggingAsync(OracleConnection conn, CancellationToken ct)
        {
            try
            {
                await using var cmd = new OracleCommand("SELECT PRIMARY_KEY FROM DBA_SUPPLEMENTAL_LOGGING", conn);
                var value = await cmd.ExecuteScalarAsync(ct) as string;
                return string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase);
            }
            catch (OracleException)
            {
                return false;
            }
        }

        private static async Task<bool> HasTablePkLoggingAsync(OracleConnection conn, string owner, string tableName, CancellationToken ct)
        {
            await using var cmd = new OracleCommand(
                "SELECT COUNT(*) FROM ALL_LOG_GROUPS WHERE OWNER = :owner AND TABLE_NAME = :tbl AND LOG_GROUP_TYPE = 'PRIMARY KEY LOGGING'", conn)
            { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("owner", owner));
            cmd.Parameters.Add(new OracleParameter("tbl", tableName));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
        }

        /// <summary>
        /// The primary key, only if Oracle enforces it. A NOVALIDATE or disabled key can hold duplicates,
        /// and a key that is not unique cannot say which destination row a delete refers to.
        /// </summary>
        internal static async Task<(IReadOnlyList<OracleKeyColumn>? Key, string? Problem)> ReadKeyAsync(OracleConnection conn, string owner, string tableName, CancellationToken ct)
        {
            const string sql = @"
                SELECT cc.column_name, tc.data_type, c.status, c.validated
                FROM all_constraints c
                JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
                JOIN all_tab_columns tc ON tc.owner = cc.owner AND tc.table_name = cc.table_name AND tc.column_name = cc.column_name
                WHERE c.owner = :owner AND c.table_name = :tbl AND c.constraint_type = 'P'
                ORDER BY cc.position";

            await using var cmd = new OracleCommand(sql, conn) { BindByName = true };
            cmd.Parameters.Add(new OracleParameter("owner", owner));
            cmd.Parameters.Add(new OracleParameter("tbl", tableName));

            var columns = new List<OracleKeyColumn>();
            string? status = null, validated = null;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    columns.Add(new OracleKeyColumn(reader.GetString(0), BaseType(reader.GetString(1))));
                    status = reader.GetString(2);
                    validated = reader.GetString(3);
                }
            }

            if (columns.Count == 0)
            {
                return (null, $"{owner}.{tableName} has no primary key, so a deleted or changed row could not be found in the destination. It copies in bulk as before but cannot track changes.");
            }

            if (!string.Equals(status, "ENABLED", StringComparison.OrdinalIgnoreCase) || !string.Equals(validated, "VALIDATED", StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"The primary key of {owner}.{tableName} is {status?.ToLowerInvariant()} and {validated?.ToLowerInvariant()}, so Oracle does not guarantee it is unique. It cannot be used to track changes.");
            }

            var unsupported = columns.Where(c => !SupportedKeyTypes.Contains(c.DataType)).ToList();
            if (unsupported.Count > 0)
            {
                return (null, $"The primary key of {owner}.{tableName} uses a type change tracking cannot match exactly ({string.Join(", ", unsupported.Select(c => $"{c.Name} {c.DataType}"))}).");
            }

            return (columns, null);
        }

        /// <summary>"TIMESTAMP(6)" and "TIMESTAMP(6) WITH TIME ZONE" are not supported keys; the rest are bare names.</summary>
        private static string BaseType(string dataType) => dataType.Trim().ToUpperInvariant();

        /// <summary>
        /// The table's and its partitions' object ids. A truncate, move, redefinition or partition
        /// exchange changes them, and the redo cannot describe what such an operation did to the rows.
        /// </summary>
        internal static async Task<string?> ReadObjectIdsAsync(OracleConnection conn, string owner, string tableName, List<string> problems, CancellationToken ct)
        {
            try
            {
                await using var cmd = new OracleCommand(@"
                    SELECT object_type, subobject_name, object_id, data_object_id
                    FROM all_objects
                    WHERE owner = :owner AND object_name = :tbl
                      AND object_type IN ('TABLE', 'TABLE PARTITION', 'TABLE SUBPARTITION')
                    ORDER BY object_id", conn)
                { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("owner", owner));
                cmd.Parameters.Add(new OracleParameter("tbl", tableName));

                var objects = new List<SourceObjectId>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    objects.Add(new SourceObjectId(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3)));
                }

                if (objects.Count == 0)
                {
                    problems.Add($"{owner}.{tableName} is not visible in ALL_OBJECTS.");
                    return null;
                }

                return JsonSerializer.Serialize(objects, TrackedTableSetup.Json);
            }
            catch (OracleException ex)
            {
                problems.Add($"Could not read the object ids of {owner}.{tableName} ({ex.Number}).");
                return null;
            }
        }

        private static async Task<decimal?> ScalarDecimalAsync(OracleConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = new OracleCommand(sql, conn);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value == null || value == DBNull.Value ? null : Convert.ToDecimal(value);
        }
    }

    /// <summary>One table, partition or subpartition as it was when the copy started.</summary>
    public sealed record SourceObjectId(string Type, string? Partition, long ObjectId, long? DataObjectId);

    /// <summary>The grants each step needs, quoted in messages so the operator knows what to ask for.</summary>
    internal static class Grants
    {
        public const string StartPoint = "The source account needs SELECT on V_$DATABASE and GV_$TRANSACTION; run \"Check change tracking\" for the exact statements.";
    }
}
