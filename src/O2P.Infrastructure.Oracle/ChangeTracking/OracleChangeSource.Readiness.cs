using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    public partial class OracleChangeSource
    {
        /// <summary>
        /// What is and is not in place, each with the SQL a DBA would run. Read-only throughout: O2P never
        /// alters the source. Every privilege is tested by using it, not by reading grant tables, because
        /// grants reach an account by roles, PUBLIC and container in ways a grant lookup misses.
        /// </summary>
        public async Task<ChangeReadiness> CheckReadinessAsync(Connection connection, string password, IReadOnlyList<(string Owner, string Table)> tables, CancellationToken cancellationToken)
        {
            if (OracleSessions.IsMock(connection))
            {
                return new ChangeReadiness(MiningMode.PluggableDatabase, "mock source", null, true,
                    new[] { new ReadinessItem("Mock source", true, "The mock source has no change history; Copy changes finds nothing to copy.", null) },
                    tables.Select(t => new TableReadiness(t.Owner, t.Table, true, Array.Empty<string>(), Array.Empty<string>())).ToList(),
                    null);
            }

            var user = connection.Username.ToUpperInvariant();
            var items = new List<ReadinessItem>();
            await using var conn = await OracleSessions.OpenUnpooledAsync(connection, password, cancellationToken);

            var (mode, layout, version) = await DetectModeAsync(conn, cancellationToken);
            items.Add(mode switch
            {
                MiningMode.Unsupported => new ReadinessItem("Database layout", false,
                    $"This source is {layout}. There, Oracle only lets the root container read the change history, as a common user (C##...), so change tracking needs a second connection. That is not available yet.",
                    null),
                _ => new ReadinessItem("Database layout", true, $"This source is {layout}{(version == null ? "" : $", Oracle {version}")}; its change history can be read over this connection.", null)
            });

            string? logMode = null, minimal = null, pk = null, force = null;
            try
            {
                await using var cmd = new OracleCommand("SELECT LOG_MODE, SUPPLEMENTAL_LOG_DATA_MIN, SUPPLEMENTAL_LOG_DATA_PK, FORCE_LOGGING FROM V$DATABASE", conn);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    logMode = reader.GetString(0);
                    minimal = reader.GetString(1);
                    pk = reader.GetString(2);
                    force = reader.GetString(3);
                }
                items.Add(Ok("Read V$DATABASE"));
            }
            catch (OracleException ex)
            {
                items.Add(Missing("Read V$DATABASE", ex, $"GRANT SELECT ON V_$DATABASE TO {user};"));
            }

            if (logMode != null)
            {
                items.Add(logMode == "ARCHIVELOG"
                    ? Ok("ARCHIVELOG mode")
                    : new ReadinessItem("ARCHIVELOG mode", false,
                        "The database is not in ARCHIVELOG mode, so its change history is overwritten as soon as the online logs are reused.",
                        "SHUTDOWN IMMEDIATE;\nSTARTUP MOUNT;\nALTER DATABASE ARCHIVELOG;\nALTER DATABASE OPEN;"));

                items.Add(minimal is "YES" or "IMPLICIT"
                    ? Ok("Minimal supplemental logging")
                    : new ReadinessItem("Minimal supplemental logging", false,
                        "Minimal supplemental logging is off, so changes to chained and migrated rows are logged incompletely.",
                        "ALTER DATABASE ADD SUPPLEMENTAL LOG DATA;"));

                var pdbPk = await HasPdbPkLoggingAsync(conn, cancellationToken);
                items.Add(pk == "YES" || pdbPk
                    ? Ok("Primary-key supplemental logging")
                    : new ReadinessItem("Primary-key supplemental logging", false,
                        "Primary-key supplemental logging is off for the database, so each table below needs it switched on for itself. Without it an update that does not change the key does not say which row it changed. It must be on before the bulk copy starts.",
                        "ALTER DATABASE ADD SUPPLEMENTAL LOG DATA (PRIMARY KEY) COLUMNS;"));

                items.Add(force == "YES"
                    ? Ok("FORCE LOGGING")
                    : new ReadinessItem("FORCE LOGGING", true,
                        "FORCE LOGGING is off. Tables below must be LOGGING, and O2P checks each copy for writes made without logging (a direct-path load, for example) and asks for a fresh bulk copy if it finds one. Turning it on removes the risk.",
                        "ALTER DATABASE FORCE LOGGING;"));
            }

            items.Add(await ProbeAsync(conn, "Read open transactions", "SELECT COUNT(*) FROM GV$TRANSACTION", $"GRANT SELECT ON GV_$TRANSACTION TO {user};", cancellationToken));
            items.Add(await ProbeAsync(conn, "Read sessions (to show who holds tracking back)", "SELECT COUNT(*) FROM GV$SESSION WHERE ROWNUM = 1", $"GRANT SELECT ON GV_$SESSION TO {user};", cancellationToken, required: false));
            if (mode == MiningMode.LogFiles)
            {
                items.Add(await ProbeAsync(conn, "Read archived-log list", "SELECT COUNT(*) FROM V$ARCHIVED_LOG WHERE ROWNUM = 1", $"GRANT SELECT ON V_$ARCHIVED_LOG TO {user};", cancellationToken));
                items.Add(await ProbeAsync(conn, "Read online-log list", "SELECT COUNT(*) FROM V$LOG l JOIN V$LOGFILE f ON f.group# = l.group# WHERE ROWNUM = 1", $"GRANT SELECT ON V_$LOG TO {user};\nGRANT SELECT ON V_$LOGFILE TO {user};", cancellationToken));
            }
            if (force != "YES")
            {
                items.Add(await ProbeAsync(conn, "Read datafile logging marks", "SELECT COUNT(*) FROM V$DATAFILE d JOIN V$TABLESPACE t ON t.ts# = d.ts# WHERE ROWNUM = 1", $"GRANT SELECT ON V_$DATAFILE TO {user};\nGRANT SELECT ON V_$TABLESPACE TO {user};", cancellationToken));
            }

            if (mode != MiningMode.Unsupported)
            {
                items.Add(await TryLogMinerAsync(conn, mode, user, cancellationToken));
            }
            else if (logMode == null)
            {
                // The layout could not be read at all, so the session cannot be tried - but the operator
                // still needs to know it is missing, and what to ask for.
                items.Add(new ReadinessItem("Start a LogMiner session", false,
                    "Could not be tried, because this account cannot read V$DATABASE to tell how the database is laid out.",
                    $"GRANT LOGMINING TO {user};\nGRANT EXECUTE ON DBMS_LOGMNR TO {user};\nGRANT SELECT ON V_$LOGMNR_CONTENTS TO {user};"));
            }

            items.Add(await UndoItemAsync(conn, cancellationToken));
            var oldest = await OldestHistoryAsync(conn, cancellationToken);

            var tableResults = new List<TableReadiness>();
            foreach (var (owner, table) in tables)
            {
                tableResults.Add(await CheckTableAsync(conn, owner, table, user, pk == "YES", cancellationToken));
            }

            var ready = items.All(i => i.Ok) && tableResults.All(t => t.Ok);
            return new ChangeReadiness(mode, layout, version?.ToString(), ready, items, tableResults, oldest);
        }

        /// <summary>The one test that proves this account can actually mine: a real, empty session.</summary>
        private static async Task<ReadinessItem> TryLogMinerAsync(OracleConnection conn, MiningMode mode, string user, CancellationToken ct)
        {
            const string name = "Start a LogMiner session";
            var fix = $"GRANT LOGMINING TO {user};\nGRANT EXECUTE ON DBMS_LOGMNR TO {user};\nGRANT SELECT ON V_$LOGMNR_CONTENTS TO {user};";
            try
            {
                var now = await ScalarDecimalAsync(conn, "SELECT CURRENT_SCN FROM V$DATABASE", ct) ?? 0;
                if (mode == MiningMode.LogFiles)
                {
                    // Without a log added first, START_LOGMNR fails with ORA-01292 even for an empty window.
                    await using var add = new OracleCommand(@"
                        BEGIN
                          FOR f IN (SELECT MIN(lf.member) m FROM v$log l JOIN v$logfile lf ON lf.group# = l.group# WHERE l.status = 'CURRENT' GROUP BY l.group#) LOOP
                            DBMS_LOGMNR.ADD_LOGFILE(LOGFILENAME => f.m, OPTIONS => DBMS_LOGMNR.NEW);
                          END LOOP;
                        END;", conn);
                    await add.ExecuteNonQueryAsync(ct);
                }

                await using (var start = new OracleCommand(
                    "BEGIN DBMS_LOGMNR.START_LOGMNR(STARTSCN => :s, ENDSCN => :s, OPTIONS => DBMS_LOGMNR.DICT_FROM_ONLINE_CATALOG); END;", conn) { BindByName = true })
                {
                    start.Parameters.Add(new OracleParameter("s", OracleDbType.Decimal) { Value = new OracleDecimal(now) });
                    await start.ExecuteNonQueryAsync(ct);
                }

                try
                {
                    await using var read = new OracleCommand("SELECT COUNT(*) FROM V$LOGMNR_CONTENTS WHERE ROWNUM = 1", conn);
                    await read.ExecuteScalarAsync(ct);
                }
                finally
                {
                    await EndLogMinerAsync(conn);
                }

                return Ok(name);
            }
            catch (OracleException ex)
            {
                return Missing(name, ex, fix);
            }
        }

        /// <summary>Reading rows AS OF an SCN needs undo to cover mining plus reading; too little raises ORA-01555.</summary>
        private static async Task<ReadinessItem> UndoItemAsync(OracleConnection conn, CancellationToken ct)
        {
            try
            {
                var retention = await ScalarDecimalAsync(conn, "SELECT TO_NUMBER(value) FROM v$parameter WHERE name = 'undo_retention'", ct);
                return new ReadinessItem("Undo retention", true,
                    $"undo_retention is {retention} seconds. A change copy reads rows as they were when it started, which needs undo to last at least as long as the copy takes; if it does not, the copy stops with ORA-01555 and can simply be run again.", null);
            }
            catch (OracleException)
            {
                return new ReadinessItem("Undo retention", true, "Could not read undo_retention (not needed, but useful: GRANT SELECT ON V_$PARAMETER).", null);
            }
        }

        /// <summary>How far back the history reaches - "press at least this often", or the history is gone.</summary>
        private static async Task<DateTimeOffset?> OldestHistoryAsync(OracleConnection conn, CancellationToken ct)
        {
            try
            {
                await using var cmd = new OracleCommand("SELECT MIN(first_time) FROM v$archived_log WHERE deleted = 'NO' AND status = 'A'", conn);
                var value = await cmd.ExecuteScalarAsync(ct);
                return value is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null;
            }
            catch (OracleException)
            {
                return null;
            }
        }

        private static async Task<TableReadiness> CheckTableAsync(OracleConnection conn, string owner, string table, string user, bool databasePkLogging, CancellationToken ct)
        {
            var problems = new List<string>();
            var fixes = new List<string>();
            string qualified;
            try
            {
                qualified = SqlIdentifier.OracleQualified(owner, table);
            }
            catch (ArgumentException)
            {
                return new TableReadiness(owner, table, false, new[] { "The table name contains characters change tracking cannot quote safely." }, Array.Empty<string>());
            }

            var (key, keyProblem) = await ReadKeyAsync(conn, owner, table, ct);
            if (keyProblem != null) problems.Add(keyProblem);

            if (!databasePkLogging && !await HasPdbPkLoggingAsync(conn, ct) && !await HasTablePkLoggingAsync(conn, owner, table, ct))
            {
                problems.Add("Primary-key supplemental logging is off for this table.");
                fixes.Add($"ALTER TABLE {qualified} ADD SUPPLEMENTAL LOG DATA (PRIMARY KEY) COLUMNS;");
            }

            try
            {
                await using var cmd = new OracleCommand(@"
                    SELECT COUNT(*) FROM (
                        SELECT logging FROM all_tables WHERE owner = :owner AND table_name = :tbl
                        UNION ALL SELECT logging FROM all_tab_partitions WHERE table_owner = :owner AND table_name = :tbl)
                    WHERE logging = 'NO'", conn) { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("owner", owner));
                cmd.Parameters.Add(new OracleParameter("tbl", table));
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0)
                {
                    problems.Add("The table (or a partition of it) is NOLOGGING, so some writes to it may leave no change history.");
                    fixes.Add($"ALTER TABLE {qualified} LOGGING;");
                }
            }
            catch (OracleException) { /* a table the account cannot see is reported by the key check */ }

            try
            {
                // Bound, not a subquery: Oracle does not allow a subquery in AS OF (ORA-22818).
                var now = await ScalarDecimalAsync(conn, "SELECT CURRENT_SCN FROM V$DATABASE", ct) ?? 0;
                await using var cmd = new OracleCommand($"SELECT COUNT(*) FROM {qualified} AS OF SCN :s WHERE ROWNUM = 0", conn);
                cmd.Parameters.Add(new OracleParameter("s", OracleDbType.Decimal) { Value = new OracleDecimal(now) });
                await cmd.ExecuteScalarAsync(ct);
            }
            catch (OracleException ex)
            {
                problems.Add($"Cannot read the table as of an earlier moment ({Describe(ex)}).");
                fixes.Add($"GRANT FLASHBACK ON {qualified} TO {user};");
            }

            // LogMiner's own verdict on what it can mine, where that view is visible (12.2+, DBA views).
            try
            {
                await using var cmd = new OracleCommand("SELECT support_mode FROM dba_goldengate_support_mode WHERE owner = :owner AND object_name = :tbl", conn) { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("owner", owner));
                cmd.Parameters.Add(new OracleParameter("tbl", table));
                var support = await cmd.ExecuteScalarAsync(ct) as string;
                if (support == "NONE")
                {
                    problems.Add("Oracle reports that its change history cannot describe this table (DBA_GOLDENGATE_SUPPORT_MODE is NONE), usually because of a column type LogMiner cannot read.");
                }
            }
            catch (OracleException) { /* not visible to this account - not required */ }

            return new TableReadiness(owner, table, problems.Count == 0, problems, fixes);
        }

        private static async Task<ReadinessItem> ProbeAsync(OracleConnection conn, string name, string sql, string fix, CancellationToken ct, bool required = true)
        {
            try
            {
                await using var cmd = new OracleCommand(sql, conn);
                await cmd.ExecuteScalarAsync(ct);
                return Ok(name);
            }
            catch (OracleException ex)
            {
                var item = Missing(name, ex, fix);
                return required ? item : item with { Ok = true, Detail = item.Detail + " Optional." };
            }
        }

        private static ReadinessItem Ok(string name) => new(name, true, "In place.", null);

        private static ReadinessItem Missing(string name, OracleException ex, string fix) =>
            new(name, false, $"Not available to this account ({Describe(ex)}).", fix);

        private static string Describe(OracleException ex) => $"ORA-{ex.Number:D5}";
    }
}
