using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    public partial class OracleChangeSource
    {
        // V$LOGMNR_CONTENTS.OPERATION_CODE values this reads.
        private const int OpInsert = 1, OpDelete = 2, OpUpdate = 3, OpDdl = 5;
        private const int OpSelectLobLocator = 9, OpLobWrite = 10, OpLobTrim = 11, OpLobErase = 28;
        private const int OpMissingScn = 34, OpUnsupported = 255;

        private static readonly int[] MinedOperations =
        {
            OpInsert, OpDelete, OpUpdate, OpDdl, OpSelectLobLocator, OpLobWrite, OpLobTrim, OpLobErase, OpMissingScn, OpUnsupported
        };

        /// <summary>LogMiner's placeholder when a row has no real address (seen on some LOB_WRITE rows).</summary>
        private const string NoRowid = "AAAAAAAAAAAAAAAAAA";

        public async Task<ScnMarks> ReadScnMarksAsync(Connection connection, string password, CancellationToken cancellationToken)
        {
            if (OracleSessions.IsMock(connection))
            {
                return new ScnMarks(2000m, null, 2000m, Array.Empty<OpenTransaction>());
            }

            await using var conn = await OracleSessions.OpenUnpooledAsync(connection, password, cancellationToken);

            // Order is the whole point - see ChangeWindow. Three separate statements, deliberately.
            var s0 = await ScalarDecimalAsync(conn, "SELECT CURRENT_SCN FROM V$DATABASE", cancellationToken)
                     ?? throw new InvalidOperationException("Oracle did not report its current SCN.");
            var open = await ReadOpenTransactionsAsync(conn, cancellationToken);
            var s1 = await ScalarDecimalAsync(conn, "SELECT CURRENT_SCN FROM V$DATABASE", cancellationToken)
                     ?? throw new InvalidOperationException("Oracle did not report its current SCN.");

            return new ScnMarks(s0, open.Count == 0 ? null : open.Min(t => t.StartScn), s1, open);
        }

        private static async Task<List<OpenTransaction>> ReadOpenTransactionsAsync(OracleConnection conn, CancellationToken ct)
        {
            // With sessions for "who is holding it back"; without if that view is not granted - the start
            // SCNs are what matter, the names are for the operator. Any failure of GV$TRANSACTION itself
            // propagates: guessing the open transactions is how an update gets lost.
            try
            {
                return await QueryOpenTransactionsAsync(conn, withSessions: true, ct);
            }
            catch (OracleException ex) when (ex.Number == 942 || ex.Number == 1031)
            {
                return await QueryOpenTransactionsAsync(conn, withSessions: false, ct);
            }
        }

        private static async Task<List<OpenTransaction>> QueryOpenTransactionsAsync(OracleConnection conn, bool withSessions, CancellationToken ct)
        {
            var sql = withSessions
                ? @"SELECT t.start_scn, s.username, s.program, s.machine, t.start_date
                    FROM gv$transaction t
                    LEFT JOIN gv$session s ON s.saddr = t.ses_addr AND s.inst_id = t.inst_id
                    ORDER BY t.start_scn"
                : "SELECT start_scn, NULL, NULL, NULL, start_date FROM gv$transaction ORDER BY start_scn";

            await using var cmd = new OracleCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<OpenTransaction>();
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0)) continue;
                list.Add(new OpenTransaction(
                    reader.GetDecimal(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc))));
            }

            return list;
        }

        public async Task<IReadOnlyList<MinedTable>> MineAsync(Connection connection, string password, IReadOnlyList<MiningTable> tables, decimal toScn, CancellationToken cancellationToken)
        {
            if (tables.Count == 0) return Array.Empty<MinedTable>();

            if (OracleSessions.IsMock(connection))
            {
                // Nothing changes in the mock source, which is exactly what a quiet table looks like.
                return tables.Select(t => new MinedTable(t.Id, Array.Empty<IReadOnlyList<string?>>(), null)).ToList();
            }

            await using var conn = await OracleSessions.OpenUnpooledAsync(connection, password, cancellationToken);
            var (mode, _, _) = await DetectModeAsync(conn, cancellationToken);
            if (mode == MiningMode.Unsupported)
            {
                throw new InvalidOperationException(
                    "This source cannot be mined over this connection. On Oracle 19c multitenant, mining must run from the root container as a common user; run \"Check change tracking\" for details.");
            }

            var from = ChangeWindow.MiningStart(tables.Select(t => t.FromScn));
            var stops = new Dictionary<long, string>();

            // Checks the history itself cannot answer, made before mining so a stopped table costs nothing.
            await CheckIncarnationAsync(conn, from, cancellationToken);
            foreach (var table in tables)
            {
                var reason = await CheckObjectsUnchangedAsync(conn, table, cancellationToken)
                             ?? await CheckNoUnloggedWritesAsync(conn, table, cancellationToken);
                if (reason != null) stops[table.Id] = reason;
            }

            var live = tables.Where(t => !stops.ContainsKey(t.Id)).ToList();
            var keys = tables.ToDictionary(t => t.Id, _ => new HashSet<string>(StringComparer.Ordinal));
            var keyValues = tables.ToDictionary(t => t.Id, _ => new List<IReadOnlyList<string?>>());
            var rowids = tables.ToDictionary(t => t.Id, _ => new HashSet<string>(StringComparer.Ordinal));

            if (live.Count > 0)
            {
                await SetMiningNlsAsync(conn, cancellationToken);
                await StartLogMinerWithRetryAsync(conn, mode, from, toScn, cancellationToken);
                try
                {
                    await ReadContentsAsync(conn, live, stops, keys, keyValues, rowids, cancellationToken);
                }
                finally
                {
                    await EndLogMinerAsync(conn);
                }
            }

            // A LOB write logs no key at all, only where the row lives. Look the key up from there, as it
            // stands at the end of the window. A row that has moved or gone since was changed again, and
            // that later change carries its own key.
            foreach (var table in live.Where(t => !stops.ContainsKey(t.Id) && rowids[t.Id].Count > 0))
            {
                foreach (var key in await KeysFromRowidsAsync(conn, table, rowids[table.Id], toScn, cancellationToken))
                {
                    AddKey(keys[table.Id], keyValues[table.Id], key);
                }
            }

            return tables.Select(t => stops.TryGetValue(t.Id, out var reason)
                    ? new MinedTable(t.Id, Array.Empty<IReadOnlyList<string?>>(), reason)
                    : new MinedTable(t.Id, keyValues[t.Id], null))
                .ToList();
        }

        /// <summary>
        /// Which way this source can be mined over this connection. A non-multitenant database picks log
        /// files by hand. A 21c+ pluggable database mines its own changes and must not - Oracle refuses
        /// ADD_LOGFILE there (ORA-65040). 19c pluggable and the root container need a second connection.
        /// </summary>
        internal static async Task<(MiningMode Mode, string Layout, int? Version)> DetectModeAsync(OracleConnection conn, CancellationToken ct)
        {
            int? version = null;
            try
            {
                await using var cmd = new OracleCommand("BEGIN :v := DBMS_DB_VERSION.VERSION; END;", conn);
                var p = cmd.Parameters.Add("v", OracleDbType.Int32, ParameterDirection.Output);
                await cmd.ExecuteNonQueryAsync(ct);
                version = ((OracleDecimal)p.Value).ToInt32();
            }
            catch (OracleException) { /* reported as unknown */ }

            string? cdb = null;
            try
            {
                await using var cmd = new OracleCommand("SELECT CDB, SYS_CONTEXT('USERENV', 'CON_NAME') FROM V$DATABASE", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    cdb = reader.GetString(0);
                    var container = reader.GetString(1);
                    if (cdb == "YES")
                    {
                        if (container == "CDB$ROOT") return (MiningMode.Unsupported, "multitenant, connected to the root container", version);
                        return version >= 21
                            ? (MiningMode.PluggableDatabase, $"multitenant, pluggable database {container}", version)
                            : (MiningMode.Unsupported, $"multitenant, pluggable database {container} on Oracle {version}", version);
                    }
                }
            }
            catch (OracleException ex) when (ex.Number == 904)
            {
                // No CDB column: Oracle 11g or earlier, which is never multitenant.
            }
            catch (OracleException)
            {
                // Cannot even read V$DATABASE, so the layout is unknown. The readiness check reports the
                // missing grant on its own line; mining would fail at the first view anyway.
                return (MiningMode.Unsupported, "of an unknown layout, because this account cannot read V$DATABASE", version);
            }

            return (MiningMode.LogFiles, "not multitenant", version);
        }

        /// <summary>
        /// After a RESETLOGS (a point-in-time recovery, or FLASHBACK DATABASE) the history before it belongs
        /// to another timeline. Rows copied from that timeline cannot be reconciled from the new one.
        /// </summary>
        private static async Task CheckIncarnationAsync(OracleConnection conn, decimal from, CancellationToken ct)
        {
            var resetlogs = await ScalarDecimalAsync(conn, "SELECT RESETLOGS_CHANGE# FROM V$DATABASE", ct);
            if (resetlogs != null && from < resetlogs.Value)
            {
                throw new ChangeHistoryGoneException(
                    "The source database was reset to an earlier point (RESETLOGS) after these tables were last copied, so their change history belongs to a timeline that no longer exists.");
            }
        }

        /// <summary>
        /// Truncate, move, shrink, online redefinition and partition exchange all give the table (or a
        /// partition) a new data-object id and log nothing row by row. New partitions are fine - interval
        /// partitioning adds them on its own, and their inserts are logged normally.
        /// </summary>
        private static async Task<string?> CheckObjectsUnchangedAsync(OracleConnection conn, MiningTable table, CancellationToken ct)
        {
            var then = JsonSerializer.Deserialize<List<SourceObjectId>>(table.ObjectIdsJson, TrackedTableSetup.Json) ?? new List<SourceObjectId>();
            var problems = new List<string>();
            var nowJson = await ReadObjectIdsAsync(conn, table.Owner, table.Table, problems, ct);
            if (nowJson == null) return $"{table.Owner}.{table.Table} can no longer be found in the source. {string.Join(" ", problems)}";

            var now = (JsonSerializer.Deserialize<List<SourceObjectId>>(nowJson, TrackedTableSetup.Json) ?? new List<SourceObjectId>())
                .ToDictionary(o => o.ObjectId);

            foreach (var old in then)
            {
                var what = old.Partition == null ? "the table" : $"partition {old.Partition}";
                if (!now.TryGetValue(old.ObjectId, out var current))
                {
                    return $"{what} of {table.Owner}.{table.Table} was dropped, exchanged or redefined since the last copy, which the change history cannot describe row by row. Run a new bulk copy.";
                }

                if (current.DataObjectId != old.DataObjectId)
                {
                    return $"{what} of {table.Owner}.{table.Table} was truncated, moved or rebuilt since the last copy, which the change history cannot describe row by row. Run a new bulk copy.";
                }
            }

            return null;
        }

        /// <summary>
        /// A NOLOGGING or direct-path write leaves no row-by-row history, only a mark on the datafile.
        /// Under FORCE LOGGING that cannot happen; otherwise look for the mark on the table's tablespaces.
        /// </summary>
        private static async Task<string?> CheckNoUnloggedWritesAsync(OracleConnection conn, MiningTable table, CancellationToken ct)
        {
            await using (var cmd = new OracleCommand("SELECT FORCE_LOGGING FROM V$DATABASE", conn))
            {
                if (string.Equals(await cmd.ExecuteScalarAsync(ct) as string, "YES", StringComparison.OrdinalIgnoreCase)) return null;
            }

            try
            {
                await using var cmd = new OracleCommand(@"
                    SELECT MAX(d.unrecoverable_change#)
                    FROM v$datafile d
                    JOIN v$tablespace ts ON ts.ts# = d.ts#
                    WHERE ts.name IN (
                        SELECT tablespace_name FROM all_tables WHERE owner = :owner AND table_name = :tbl
                        UNION SELECT tablespace_name FROM all_tab_partitions WHERE table_owner = :owner AND table_name = :tbl
                        UNION SELECT tablespace_name FROM all_tab_subpartitions WHERE table_owner = :owner AND table_name = :tbl)", conn)
                { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("owner", table.Owner));
                cmd.Parameters.Add(new OracleParameter("tbl", table.Table));
                var value = await cmd.ExecuteScalarAsync(ct);
                if (value != null && value != DBNull.Value && Convert.ToDecimal(value) > table.FromScn)
                {
                    return $"Something was written to {table.Owner}.{table.Table}'s storage without logging (NOLOGGING or a direct-path load) since the last copy, so the change history is incomplete. Run a new bulk copy.";
                }

                return null;
            }
            catch (OracleException ex)
            {
                return $"Could not confirm that nothing was written to {table.Owner}.{table.Table} without logging ({ex.Number}). Turn on FORCE LOGGING, or grant SELECT ON V_$DATAFILE and V_$TABLESPACE.";
            }
        }

        /// <summary>
        /// MINE_VALUE renders keys using the session's NLS formats, so they are fixed here. This session is
        /// unpooled and closed afterwards; the re-read uses a different one and keeps the defaults.
        /// </summary>
        private static async Task SetMiningNlsAsync(OracleConnection conn, CancellationToken ct)
        {
            await using var cmd = new OracleCommand(
                $"ALTER SESSION SET NLS_DATE_FORMAT = '{OracleKeyBinding.DateFormatOracle}' NLS_NUMERIC_CHARACTERS = '.,'", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task StartLogMinerWithRetryAsync(OracleConnection conn, MiningMode mode, decimal from, decimal to, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (mode == MiningMode.LogFiles) await AddLogFilesAsync(conn, from, to, ct);

                    await using var cmd = new OracleCommand(
                        "BEGIN DBMS_LOGMNR.START_LOGMNR(STARTSCN => :s, ENDSCN => :e, OPTIONS => DBMS_LOGMNR.DICT_FROM_ONLINE_CATALOG); END;", conn)
                    { BindByName = true };
                    // The window is (from, to]; everything at or before "from" is already in the destination.
                    cmd.Parameters.Add(new OracleParameter("s", OracleDbType.Decimal) { Value = new OracleDecimal(from + 1) });
                    cmd.Parameters.Add(new OracleParameter("e", OracleDbType.Decimal) { Value = new OracleDecimal(to) });
                    await cmd.ExecuteNonQueryAsync(ct);
                    return;
                }
                catch (OracleException ex) when (ex.Number is 1291 or 1292)
                {
                    throw new ChangeHistoryGoneException(
                        "The change history these tables need is no longer on the source server - its archived logs have been removed. Run a new bulk copy, and keep archived logs for longer than the gap between presses of Copy changes.", ex);
                }
                catch (OracleException ex) when (attempt < 3 && ex.Number is 310 or 334 or 368)
                {
                    // An online log was reused while being added. Pick the files again.
                    await EndLogMinerAsync(conn);
                }
            }
        }

        /// <summary>
        /// Log files covering (from, to], picked by hand - only for a database that is not multitenant.
        /// Current incarnation only, not deleted, one copy per sequence; an online log that has already been
        /// archived is taken once (adding both fails with ORA-01289). A missing sequence means a hole.
        /// </summary>
        private static async Task AddLogFilesAsync(OracleConnection conn, decimal from, decimal to, CancellationToken ct)
        {
            var logs = new Dictionary<(int Thread, long Sequence), (string File, decimal First, decimal Next)>();

            await using (var cmd = new OracleCommand(@"
                SELECT thread#, sequence#, name, first_change#, next_change#
                FROM v$archived_log
                WHERE resetlogs_change# = (SELECT resetlogs_change# FROM v$database)
                  AND standby_dest = 'NO' AND deleted = 'NO' AND status = 'A' AND name IS NOT NULL
                  AND next_change# > :from AND first_change# <= :to
                ORDER BY thread#, sequence#, dest_id", conn) { BindByName = true })
            {
                cmd.Parameters.Add(new OracleParameter("from", OracleDbType.Decimal) { Value = new OracleDecimal(from) });
                cmd.Parameters.Add(new OracleParameter("to", OracleDbType.Decimal) { Value = new OracleDecimal(to) });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var key = (reader.GetInt32(0), reader.GetInt64(1));
                    if (!logs.ContainsKey(key)) logs[key] = (reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4));
                }
            }

            await using (var cmd = new OracleCommand(@"
                SELECT l.thread#, l.sequence#, MIN(f.member), l.first_change#, l.next_change#
                FROM v$log l JOIN v$logfile f ON f.group# = l.group#
                WHERE f.type = 'ONLINE' AND l.first_change# <= :to AND (l.next_change# > :from OR l.status = 'CURRENT')
                GROUP BY l.thread#, l.sequence#, l.first_change#, l.next_change#", conn) { BindByName = true })
            {
                cmd.Parameters.Add(new OracleParameter("from", OracleDbType.Decimal) { Value = new OracleDecimal(from) });
                cmd.Parameters.Add(new OracleParameter("to", OracleDbType.Decimal) { Value = new OracleDecimal(to) });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var key = (reader.GetInt32(0), reader.GetInt64(1));
                    // Prefer the archived copy: an online log can be reused under our feet.
                    if (!logs.ContainsKey(key)) logs[key] = (reader.GetString(2), reader.GetDecimal(3), reader.IsDBNull(4) ? decimal.MaxValue : reader.GetDecimal(4));
                }
            }

            if (logs.Count == 0 || logs.Values.Min(l => l.First) > from + 1)
            {
                throw new ChangeHistoryGoneException(
                    "The change history these tables need is no longer on the source server - its archived logs have been removed. Run a new bulk copy, and keep archived logs for longer than the gap between presses of Copy changes.");
            }

            foreach (var thread in logs.Keys.GroupBy(k => k.Thread))
            {
                var sequences = thread.Select(k => k.Sequence).OrderBy(s => s).ToList();
                for (var i = 1; i < sequences.Count; i++)
                {
                    if (sequences[i] != sequences[i - 1] + 1)
                    {
                        throw new ChangeHistoryGoneException(
                            $"Archived log {sequences[i - 1] + 1} of thread {thread.Key} is missing from the source server, so part of the change history is gone. Run a new bulk copy.");
                    }
                }
            }

            var first = true;
            foreach (var log in logs.OrderBy(l => l.Key.Thread).ThenBy(l => l.Key.Sequence))
            {
                await using var cmd = new OracleCommand("BEGIN DBMS_LOGMNR.ADD_LOGFILE(LOGFILENAME => :f, OPTIONS => :o); END;", conn) { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("f", log.Value.File));
                cmd.Parameters.Add(new OracleParameter("o", OracleDbType.Int32) { Value = first ? 1 /* NEW */ : 3 /* ADDFILE */ });
                await cmd.ExecuteNonQueryAsync(ct);
                first = false;
            }
        }

        private static async Task EndLogMinerAsync(OracleConnection conn)
        {
            try
            {
                await using var cmd = new OracleCommand("BEGIN DBMS_LOGMNR.END_LOGMNR; END;", conn);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (OracleException ex) when (ex.Number == 1307)
            {
                // No session active - nothing to end.
            }
        }

        /// <summary>
        /// One streaming query over the whole window - every query against V$LOGMNR_CONTENTS re-reads all
        /// the redo. Only the key values are extracted, via MINE_VALUE; the reconstructed SQL text is the
        /// expensive part of LogMiner and is never selected.
        /// </summary>
        private static async Task ReadContentsAsync(
            OracleConnection conn,
            IReadOnlyList<MiningTable> tables,
            Dictionary<long, string> stops,
            Dictionary<long, HashSet<string>> keys,
            Dictionary<long, List<IReadOnlyList<string?>>> keyValues,
            Dictionary<long, HashSet<string>> rowids,
            CancellationToken ct)
        {
            var byName = tables.ToDictionary(t => (t.Owner, t.Table));
            var byObject = new Dictionary<long, MiningTable>();
            foreach (var t in tables)
            {
                foreach (var o in JsonSerializer.Deserialize<List<SourceObjectId>>(t.ObjectIdsJson, TrackedTableSetup.Json) ?? new())
                {
                    byObject[o.ObjectId] = t;
                }
            }

            var slots = tables.Max(t => t.Key.Count);
            var select = new StringBuilder("SELECT operation_code, scn, seg_owner, table_name, status, info, row_id, data_obj#");
            for (var s = 0; s < slots; s++)
            {
                select.Append(", ").Append(KeySlot("MINE_VALUE", "redo_value", tables, s));
                select.Append(", ").Append(KeySlot("COLUMN_PRESENT", "redo_value", tables, s));
                select.Append(", ").Append(KeySlot("MINE_VALUE", "undo_value", tables, s));
                select.Append(", ").Append(KeySlot("COLUMN_PRESENT", "undo_value", tables, s));
            }

            // Names as binds; object ids are numbers we read ourselves, so they go in as literals.
            var nameTuples = string.Join(", ", tables.Select((_, i) => $"(:o{i}, :t{i})"));
            var objectIds = byObject.Count == 0 ? "" : $" OR data_obj# IN ({string.Join(", ", byObject.Keys)})";
            select.Append($@"
                FROM v$logmnr_contents
                WHERE operation_code IN ({string.Join(", ", MinedOperations)})
                  AND ((seg_owner, table_name) IN ({nameTuples}){objectIds} OR operation_code = {OpMissingScn})");

            await using var cmd = new OracleCommand(select.ToString(), conn) { BindByName = true, FetchSize = 4 * 1024 * 1024 };
            for (var i = 0; i < tables.Count; i++)
            {
                cmd.Parameters.Add(new OracleParameter($"o{i}", tables[i].Owner));
                cmd.Parameters.Add(new OracleParameter($"t{i}", tables[i].Table));
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var op = Convert.ToInt32(reader.GetValue(0));
                var scn = reader.GetDecimal(1);
                var owner = reader.IsDBNull(2) ? null : reader.GetString(2);
                var name = reader.IsDBNull(3) ? null : reader.GetString(3);
                var status = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
                var info = reader.IsDBNull(5) ? null : reader.GetString(5);
                var rowid = reader.IsDBNull(6) ? null : reader.GetString(6);
                long? objectId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7));

                if (op == OpMissingScn)
                {
                    // LogMiner's own report of a hole in the logs it was given.
                    throw new ChangeHistoryGoneException("Part of the source's change history is missing (LogMiner reported a missing SCN range). Run a new bulk copy.");
                }

                MiningTable? table = null;
                if (owner != null && name != null) byName.TryGetValue((owner, name), out table);
                if (table == null && objectId != null) byObject.TryGetValue(objectId.Value, out table);
                if (table == null || stops.ContainsKey(table.Id)) continue;
                if (scn <= table.FromScn) continue; // already applied for this table

                // Anything the history cannot describe stops the table - never skipped silently. A row
                // named only "OBJ# n" or flagged with a dictionary mismatch comes from before a structural
                // change, and MINE_VALUE returns nothing for it.
                if (op == OpUnsupported || status != 0 || (name != null && name.StartsWith("OBJ# ", StringComparison.Ordinal))
                    || (info != null && info.Contains("Dictionary Version Mismatch", StringComparison.OrdinalIgnoreCase)))
                {
                    stops[table.Id] = $"The change history for {table.Owner}.{table.Table} contains a change it cannot describe (operation {op}{(string.IsNullOrWhiteSpace(info) ? "" : ", " + info.Trim())}). Run a new bulk copy.";
                    continue;
                }

                if (op == OpDdl)
                {
                    stops[table.Id] = $"{table.Owner}.{table.Table} had a structural change (for example a truncate or a column added) since the last copy, which the change history cannot describe row by row. Run a new bulk copy.";
                    continue;
                }

                var oldKey = ReadKey(reader, table, undo: true, slots);
                var newKey = ReadKey(reader, table, undo: false, slots);
                if (oldKey != null) AddKey(keys[table.Id], keyValues[table.Id], oldKey);
                if (newKey != null) AddKey(keys[table.Id], keyValues[table.Id], newKey);

                if (oldKey == null && newKey == null)
                {
                    if (rowid != null && rowid != NoRowid)
                    {
                        rowids[table.Id].Add(rowid);
                    }
                    else if (op is OpInsert or OpDelete or OpUpdate)
                    {
                        // A row change that names neither a key nor a row cannot be matched to anything.
                        stops[table.Id] = $"A change to {table.Owner}.{table.Table} was logged without its primary key. Check that primary-key supplemental logging is on, then run a new bulk copy.";
                    }
                    // A LOB write on the placeholder address: its companion SEL_LOB_LOCATOR carries the real one.
                }
            }
        }

        /// <summary>
        /// One key column for every table at once: CASE on the table, then MINE_VALUE / COLUMN_PRESENT with
        /// that table's column. Names were validated by SqlIdentifier and are quoted, so they are literals
        /// safely; MINE_VALUE's argument is case-sensitive, which the quoted form also satisfies.
        /// </summary>
        private static string KeySlot(string function, string column, IReadOnlyList<MiningTable> tables, int slot)
        {
            var sb = new StringBuilder("CASE seg_owner || '.' || table_name");
            foreach (var t in tables.Where(t => slot < t.Key.Count))
            {
                var path = $"{SqlIdentifier.QuoteOracle(t.Owner)}.{SqlIdentifier.QuoteOracle(t.Table)}.{SqlIdentifier.QuoteOracle(t.Key[slot].Name)}";
                sb.Append($" WHEN '{t.Owner}.{t.Table}' THEN TO_CHAR(DBMS_LOGMNR.{function}({column}, '{path}'))");
            }
            return sb.Append(" END").ToString();
        }

        /// <summary>A whole key, or null unless every column of it was present in that half of the change.</summary>
        private static IReadOnlyList<string?>? ReadKey(OracleDataReader reader, MiningTable table, bool undo, int slots)
        {
            var values = new string?[table.Key.Count];
            for (var c = 0; c < table.Key.Count; c++)
            {
                var baseIndex = 8 + c * 4 + (undo ? 2 : 0);
                var present = reader.IsDBNull(baseIndex + 1) ? null : reader.GetString(baseIndex + 1);
                if (present != "1") return null;
                values[c] = reader.IsDBNull(baseIndex) ? null : reader.GetString(baseIndex);
            }

            // A primary key has no nulls; a null here means the value was not really logged.
            return values.Any(v => v == null) ? null : values;
        }

        private static void AddKey(HashSet<string> seen, List<IReadOnlyList<string?>> list, IReadOnlyList<string?> key)
        {
            if (seen.Add(string.Join("\u0001", key))) list.Add(key);
        }

        /// <summary>Keys for rows known only by address - LOB writes - as they stand at the end of the window.</summary>
        private static async Task<List<IReadOnlyList<string?>>> KeysFromRowidsAsync(OracleConnection conn, MiningTable table, HashSet<string> rowids, decimal asOf, CancellationToken ct)
        {
            var result = new List<IReadOnlyList<string?>>();
            var keyColumns = string.Join(", ", table.Key.Select(OracleKeyBinding.SelectAsText));

            foreach (var batch in rowids.Chunk(OracleKeyBinding.BatchSize))
            {
                var binds = string.Join(", ", Enumerable.Range(0, OracleKeyBinding.BatchSize).Select(i => $"CHARTOROWID(:r{i})"));
                await using var cmd = new OracleCommand(
                    $"SELECT {keyColumns} FROM {SqlIdentifier.OracleQualified(table.Owner, table.Table)} AS OF SCN :asof WHERE ROWID IN ({binds})", conn)
                { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("asof", OracleDbType.Decimal) { Value = new OracleDecimal(asOf) });
                for (var i = 0; i < OracleKeyBinding.BatchSize; i++)
                {
                    cmd.Parameters.Add(new OracleParameter($"r{i}", batch[Math.Min(i, batch.Length - 1)]));
                }

                try
                {
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var key = new string?[table.Key.Count];
                        for (var c = 0; c < key.Length; c++) key[c] = reader.IsDBNull(c) ? null : Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture);
                        result.Add(key);
                    }
                }
                catch (OracleException ex)
                {
                    throw OracleErrors.ForAsOfRead(ex, table.Owner, table.Table);
                }
            }

            return result;
        }
    }
}
