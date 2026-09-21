using Npgsql;
using NpgsqlTypes;
using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Infrastructure.Postgres.Schema;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Writer
{
    /// <summary>
    /// Makes a live destination table match Oracle for a set of keys: rows that exist are written, keys
    /// with no row are deleted. Nothing here creates, alters (bar the one-off primary key after settling)
    /// or empties a table.
    /// </summary>
    public class PostgresChangeApplier : IPostgresChangeApplier
    {
        private readonly IPostgresSchemaInspector _inspector;

        public PostgresChangeApplier(IPostgresSchemaInspector inspector)
        {
            _inspector = inspector;
        }

        public async Task<IPostgresChangeSession> OpenAsync(ChangeTarget target, bool settle, CancellationToken cancellationToken)
        {
            if (PostgresConnectionFactory.IsMock(target.Connection)) return new MockSession();

            var live = await _inspector.GetTableColumnsAsync(target.Connection, target.Password, target.Schema, target.Table, cancellationToken);
            if (live == null || live.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The destination table {target.Schema}.{target.Table} is not there any more. Run a new bulk copy to recreate it.");
            }

            var liveByName = live.ToDictionary(c => c.ColumnName, StringComparer.Ordinal);
            var columns = target.Columns.Select(c => PostgresName.TargetColumn(c.Name, target.NameStyle)).ToList();
            var missing = columns.Where(c => !liveByName.ContainsKey(c)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The destination table {target.Schema}.{target.Table} no longer has column(s) {string.Join(", ", missing.Select(m => $"\"{m}\""))}. Run a new bulk copy.");
            }

            var keyColumns = target.Key.Select(k => PostgresName.TargetColumn(k.Name, target.NameStyle)).ToList();

            // Unpooled: the scratch tables live for the session, and a pooled connection would carry them
            // into whoever borrowed it next.
            var conn = await PostgresConnectionFactory.OpenAsync(target.Connection, target.Password, cancellationToken);
            try
            {
                if (!settle && !await HasArbiterIndexAsync(conn, target.Schema, target.Table, keyColumns, cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"The destination table {target.Schema}.{target.Table} has no primary key or unique index on exactly ({string.Join(", ", keyColumns)}), so changed rows cannot be matched to existing ones. " +
                        $"Add one, for example: ALTER TABLE {SqlIdentifier.QuotePostgresQualified(target.Schema, target.Table)} ADD PRIMARY KEY ({string.Join(", ", keyColumns.Select(SqlIdentifier.QuotePostgres))});");
                }

                var session = new Session(conn, target, settle, columns, keyColumns, liveByName);
                await session.CreateScratchTablesAsync(cancellationToken);
                return session;
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public async Task<IReadOnlyList<string>> OrderParentsFirstAsync(O2P.Domain.Entities.Connection connection, string password, string schema, IReadOnlyList<string> tables, CancellationToken cancellationToken)
        {
            if (PostgresConnectionFactory.IsMock(connection) || tables.Count < 2) return tables;

            var edges = new List<(string Child, string Parent)>();
            await using (var conn = await PostgresConnectionFactory.OpenAsync(connection, password, cancellationToken))
            await using (var cmd = new NpgsqlCommand(@"
                SELECT child.relname, parent.relname
                FROM pg_constraint k
                JOIN pg_class child ON child.oid = k.conrelid
                JOIN pg_class parent ON parent.oid = k.confrelid
                JOIN pg_namespace n ON n.oid = child.relnamespace
                WHERE k.contype = 'f' AND n.nspname = @schema
                  AND child.relname = ANY(@tables) AND parent.relname = ANY(@tables)
                  AND child.oid <> parent.oid", conn))
            {
                cmd.Parameters.AddWithValue("schema", schema);
                cmd.Parameters.AddWithValue("tables", tables.ToArray());
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) edges.Add((reader.GetString(0), reader.GetString(1)));
            }

            // Repeatedly take the tables whose parents are all placed; whatever is left is a cycle.
            var ordered = new List<string>();
            var remaining = tables.ToList();
            while (remaining.Count > 0)
            {
                var ready = remaining.Where(t => !edges.Any(e => e.Child == t && remaining.Contains(e.Parent))).ToList();
                if (ready.Count == 0) { ordered.AddRange(remaining); break; }
                ordered.AddRange(ready);
                remaining.RemoveAll(ready.Contains);
            }

            return ordered;
        }

        /// <summary>
        /// ON CONFLICT needs a unique index on exactly these columns that PostgreSQL will accept as an
        /// arbiter: valid, not deferrable (deferrable constraints cannot arbitrate), no WHERE and no
        /// expressions. Any column order.
        /// </summary>
        private static async Task<bool> HasArbiterIndexAsync(NpgsqlConnection conn, string schema, string table, IReadOnlyList<string> keyColumns, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT array_agg(a.attname::text ORDER BY a.attname)
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(i.indkey)
                WHERE n.nspname = @schema AND c.relname = @table
                  AND i.indisunique AND i.indisvalid AND i.indimmediate
                  AND i.indpred IS NULL AND i.indexprs IS NULL
                GROUP BY i.indexrelid", conn);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);

            var wanted = keyColumns.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var columns = reader.GetFieldValue<string[]>(0).OrderBy(k => k, StringComparer.Ordinal).ToArray();
                if (columns.SequenceEqual(wanted, StringComparer.Ordinal)) return true;
            }

            return false;
        }

        private sealed class MockSession : IPostgresChangeSession
        {
            public Task<ApplyResult> ApplyAsync(IReadOnlyList<IReadOnlyList<string?>> keys, IReadOnlyList<object[]> rows, CancellationToken cancellationToken) =>
                Task.FromResult(new ApplyResult(rows.Count, 0));

            public Task<string?> FinishSettlingAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Session : IPostgresChangeSession
        {
            /// <summary>A busy live table is never waited on indefinitely; the batch fails and is simply re-run.</summary>
            private const string LockTimeout = "30s";
            private const int StatementTimeoutSeconds = 1800;

            private readonly NpgsqlConnection _conn;
            private readonly ChangeTarget _target;
            private readonly bool _settle;
            private readonly IReadOnlyList<string> _columns;
            private readonly IReadOnlyList<string> _keyColumns;
            private readonly IReadOnlyDictionary<string, PostgresLiveColumn> _live;
            private readonly PostgresWritePlan[] _plans;
            private readonly string _table;

            public Session(NpgsqlConnection conn, ChangeTarget target, bool settle, IReadOnlyList<string> columns,
                IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, PostgresLiveColumn> live)
            {
                _conn = conn;
                _target = target;
                _settle = settle;
                _columns = columns;
                _keyColumns = keyColumns;
                _live = live;
                // The same plan the bulk copy used, from the same types - see PostgresWritePlan.
                _plans = PostgresWritePlan.ResolveAll(target.Columns.Select(c => c.PostgresType));
                _table = SqlIdentifier.QuotePostgresQualified(target.Schema, target.Table);
            }

            /// <summary>
            /// Created once per session and emptied by every commit. Typed from the destination's own
            /// columns - not LIKE, which would bring defaults, NOT NULL and lose identity semantics - and
            /// all nullable, so a scratch row can hold whatever the source row holds.
            /// </summary>
            public async Task CreateScratchTablesAsync(CancellationToken ct)
            {
                var keys = string.Join(", ", _keyColumns.Select((_, i) => $"c{i} text"));
                var rows = string.Join(", ", _columns.Select(c => $"{SqlIdentifier.QuotePostgres(c)} {_live[c].FormattedType}"));
                await ExecAsync(null, $@"
                    CREATE TEMP TABLE _o2p_keys ({keys}) ON COMMIT DELETE ROWS;
                    CREATE TEMP TABLE _o2p_rows ({rows}) ON COMMIT DELETE ROWS;", ct);
            }

            public async Task<ApplyResult> ApplyAsync(IReadOnlyList<IReadOnlyList<string?>> keys, IReadOnlyList<object[]> rows, CancellationToken cancellationToken)
            {
                if (keys.Count == 0) return new ApplyResult(0, 0);

                await using var tx = await _conn.BeginTransactionAsync(cancellationToken);
                await ExecAsync(tx, $"SET LOCAL lock_timeout = '{LockTimeout}'", cancellationToken);

                await using (var importer = await _conn.BeginBinaryImportAsync(
                    $"COPY _o2p_keys ({string.Join(", ", _keyColumns.Select((_, i) => $"c{i}"))}) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    foreach (var key in keys)
                    {
                        await importer.StartRowAsync(cancellationToken);
                        foreach (var value in key)
                        {
                            if (value == null) await importer.WriteNullAsync(cancellationToken);
                            else await importer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
                        }
                    }
                    await importer.CompleteAsync(cancellationToken);
                }

                var columnList = string.Join(", ", _columns.Select(SqlIdentifier.QuotePostgres));
                if (rows.Count > 0)
                {
                    await using var importer = await _conn.BeginBinaryImportAsync($"COPY _o2p_rows ({columnList}) FROM STDIN (FORMAT BINARY)", cancellationToken);
                    foreach (var row in rows) await PostgresWritePlan.WriteRowAsync(importer, row, _plans, cancellationToken);
                    await importer.CompleteAsync(cancellationToken);
                }

                var matchesKey = string.Join(" AND ", _keyColumns.Select((c, i) => $"t.{SqlIdentifier.QuotePostgres(c)} = {KeyCast(c, i)}"));
                var rowHasKey = string.Join(" AND ", _keyColumns.Select(c => $"r.{SqlIdentifier.QuotePostgres(c)} = t.{SqlIdentifier.QuotePostgres(c)}"));

                // Keys whose row is gone in Oracle (deleted, or moved out of the row filter).
                var deleted = await ExecAsync(tx,
                    $"DELETE FROM {_table} t USING _o2p_keys k WHERE {matchesKey} AND NOT EXISTS (SELECT 1 FROM _o2p_rows r WHERE {rowHasKey})",
                    cancellationToken);

                long written;
                var overriding = _columns.Any(c => _live[c].IsIdentity) ? " OVERRIDING SYSTEM VALUE" : "";
                if (_settle)
                {
                    // No key yet, so no ON CONFLICT. Clear every copy of the keys that still have a row -
                    // including a row the fuzzy load may have copied twice - then insert each once.
                    await ExecAsync(tx,
                        $"DELETE FROM {_table} t USING _o2p_keys k WHERE {matchesKey} AND EXISTS (SELECT 1 FROM _o2p_rows r WHERE {rowHasKey})",
                        cancellationToken);
                    written = await ExecAsync(tx, $"INSERT INTO {_table} ({columnList}){overriding} SELECT {columnList} FROM _o2p_rows", cancellationToken);
                }
                else
                {
                    written = await ExecAsync(tx, UpsertSql(columnList, overriding), cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                return new ApplyResult(written, deleted);
            }

            /// <summary>
            /// Updates skip rows whose values did not really change, so triggers do not fire and no WAL is
            /// written for nothing. Identity columns are never in the SET list: GENERATED ALWAYS refuses it.
            /// </summary>
            private string UpsertSql(string columnList, string overriding)
            {
                var settable = _columns
                    .Where(c => !_keyColumns.Contains(c, StringComparer.Ordinal) && !_live[c].IsIdentity && !_live[c].IsGenerated)
                    .Select(SqlIdentifier.QuotePostgres)
                    .ToList();

                var conflict = string.Join(", ", _keyColumns.Select(SqlIdentifier.QuotePostgres));
                var insert = $"INSERT INTO {_table} AS t ({columnList}){overriding} SELECT {columnList} FROM _o2p_rows ON CONFLICT ({conflict})";
                if (settable.Count == 0) return insert + " DO NOTHING";

                var set = string.Join(", ", settable.Select(c => $"{c} = EXCLUDED.{c}"));
                var current = string.Join(", ", settable.Select(c => $"t.{c}"));
                var incoming = string.Join(", ", settable.Select(c => $"EXCLUDED.{c}"));
                return $"{insert} DO UPDATE SET {set} WHERE ({current}) IS DISTINCT FROM ({incoming})";
            }

            /// <summary>Keys arrive as text; this turns one back into the destination column's own type.</summary>
            private string KeyCast(string column, int index)
            {
                var type = _live[column].FormattedType;
                return type == "bytea" ? $"decode(k.c{index}, 'hex')" : $"k.c{index}::{type}";
            }

            public async Task<string?> FinishSettlingAsync(CancellationToken cancellationToken)
            {
                if (!_settle) return null;

                var keys = string.Join(", ", _keyColumns.Select(SqlIdentifier.QuotePostgres));
                await using (var dup = new NpgsqlCommand(
                    $"SELECT {keys} FROM {_table} GROUP BY {keys} HAVING count(*) > 1 LIMIT 5", _conn) { CommandTimeout = StatementTimeoutSeconds })
                await using (var reader = await dup.ExecuteReaderAsync(cancellationToken))
                {
                    var examples = new List<string>();
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        examples.Add("(" + string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i)))) + ")");
                    }

                    if (examples.Count > 0)
                    {
                        return $"The destination table still holds more than one row for some keys after settling, for example {string.Join(", ", examples)}, so it cannot be trusted to match the source. Run a new bulk copy.";
                    }
                }

                // Built concurrently so the table stays usable, then promoted to the primary key, which
                // only needs a brief lock. CONCURRENTLY cannot run inside a transaction, and none is open.
                var name = await FreeIndexNameAsync(cancellationToken);
                var quotedName = SqlIdentifier.QuotePostgres(name);
                try
                {
                    await ExecAsync(null, $"CREATE UNIQUE INDEX CONCURRENTLY {quotedName} ON {_table} ({keys})", cancellationToken, timeoutSeconds: 0);
                }
                catch
                {
                    // A failed concurrent build leaves an INVALID index behind; do not leave litter.
                    try { await ExecAsync(null, $"DROP INDEX CONCURRENTLY IF EXISTS {SqlIdentifier.QuotePostgresQualified(_target.Schema, name)}", CancellationToken.None); }
                    catch { /* best effort */ }
                    throw;
                }

                await ExecAsync(null, $"ALTER TABLE {_table} ADD CONSTRAINT {quotedName} PRIMARY KEY USING INDEX {quotedName}", cancellationToken);
                return null;
            }

            /// <summary>&lt;table&gt;_pkey in lower case, cut to 63 bytes; if that name is taken in the schema, a variant.</summary>
            private async Task<string> FreeIndexNameAsync(CancellationToken ct)
            {
                foreach (var candidate in new[] { $"{_target.Table}_pkey", $"{_target.Table}_o2p_pkey" })
                {
                    var name = PostgresName.Truncate63(PostgresName.For(candidate));
                    await using var cmd = new NpgsqlCommand(
                        "SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @s AND c.relname = @n", _conn);
                    cmd.Parameters.AddWithValue("s", _target.Schema);
                    cmd.Parameters.AddWithValue("n", name);
                    if (await cmd.ExecuteScalarAsync(ct) == null) return name;
                }

                throw new InvalidOperationException($"Could not find a free name for the primary key of {_target.Schema}.{_target.Table}.");
            }

            private async Task<int> ExecAsync(NpgsqlTransaction? tx, string sql, CancellationToken ct, int timeoutSeconds = StatementTimeoutSeconds)
            {
                await using var cmd = new NpgsqlCommand(sql, _conn, tx) { CommandTimeout = timeoutSeconds };
                return await cmd.ExecuteNonQueryAsync(ct);
            }

            public ValueTask DisposeAsync() => _conn.DisposeAsync();
        }
    }
}
