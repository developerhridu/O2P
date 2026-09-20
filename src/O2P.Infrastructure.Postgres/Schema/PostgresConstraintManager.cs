using Npgsql;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Schema
{
    public class PostgresConstraintManager : IPostgresConstraintManager
    {
        public async Task<bool> TableExistsAsync(Connection connection, string password, string schema, string table, CancellationToken cancellationToken)
        {
            if (IsMock(connection)) return false;

            await using var conn = await OpenAsync(connection, password, cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT EXISTS (
    SELECT 1
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = @schema
      AND c.relname = @table
      AND c.relkind IN ('r', 'p')
);";
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is true;
        }

        public async Task<PostgresConstraintSnapshot> SnapshotAsync(Connection connection, string password, string schema, string table, CancellationToken cancellationToken)
        {
            var snapshot = new PostgresConstraintSnapshot
            {
                SchemaName = schema,
                TableName = table
            };

            if (IsMock(connection)) return snapshot;

            await using var conn = await OpenAsync(connection, password, cancellationToken);

            // Local PK / UNIQUE / FK / CHECK on the target table.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT c.conname,
       c.contype::text,
       pg_get_constraintdef(c.oid, true) AS definition
FROM pg_constraint c
JOIN pg_class t ON t.oid = c.conrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE n.nspname = @schema
  AND t.relname = @table
  AND c.contype IN ('p', 'u', 'f', 'c')
ORDER BY
  CASE c.contype
    WHEN 'f' THEN 1
    WHEN 'c' THEN 2
    WHEN 'u' THEN 3
    WHEN 'p' THEN 4
    ELSE 5
  END,
  c.conname;";
                cmd.Parameters.AddWithValue("schema", schema);
                cmd.Parameters.AddWithValue("table", table);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(0);
                    var type = reader.GetString(1);
                    var definition = reader.GetString(2);
                    var quotedTable = SqlIdentifier.QuotePostgresQualified(schema, table);
                    var quotedName = SqlIdentifier.QuotePostgres(name);
                    snapshot.Constraints.Add(new PostgresConstraintEntry
                    {
                        ConstraintName = name,
                        ConstraintType = type,
                        IsInbound = false,
                        OwnerSchema = schema,
                        OwnerTable = table,
                        Definition = definition,
                        DropSql = $"ALTER TABLE {quotedTable} DROP CONSTRAINT IF EXISTS {quotedName};",
                        CreateSql = $"ALTER TABLE {quotedTable} ADD CONSTRAINT {quotedName} {definition};"
                    });
                }
            }

            // Inbound FKs that reference the target table (must drop before PK/UNIQUE).
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT rn.nspname AS owner_schema,
       rt.relname AS owner_table,
       c.conname,
       c.contype::text,
       pg_get_constraintdef(c.oid, true) AS definition
FROM pg_constraint c
JOIN pg_class t ON t.oid = c.confrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
JOIN pg_class rt ON rt.oid = c.conrelid
JOIN pg_namespace rn ON rn.oid = rt.relnamespace
WHERE n.nspname = @schema
  AND t.relname = @table
  AND c.contype = 'f'
ORDER BY rn.nspname, rt.relname, c.conname;";
                cmd.Parameters.AddWithValue("schema", schema);
                cmd.Parameters.AddWithValue("table", table);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var ownerSchema = reader.GetString(0);
                    var ownerTable = reader.GetString(1);
                    var name = reader.GetString(2);
                    var type = reader.GetString(3);
                    var definition = reader.GetString(4);
                    var quotedOwner = SqlIdentifier.QuotePostgresQualified(ownerSchema, ownerTable);
                    var quotedName = SqlIdentifier.QuotePostgres(name);
                    snapshot.Constraints.Add(new PostgresConstraintEntry
                    {
                        ConstraintName = name,
                        ConstraintType = type,
                        IsInbound = true,
                        OwnerSchema = ownerSchema,
                        OwnerTable = ownerTable,
                        Definition = definition,
                        DropSql = $"ALTER TABLE {quotedOwner} DROP CONSTRAINT IF EXISTS {quotedName};",
                        CreateSql = $"ALTER TABLE {quotedOwner} ADD CONSTRAINT {quotedName} {definition};"
                    });
                }
            }

            return snapshot;
        }

        public async Task DropAsync(Connection connection, string password, PostgresConstraintSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (IsMock(connection) || snapshot.Constraints.Count == 0) return;

            // Inbound FKs first, then local FKs, CHECKs, UNIQUE, PRIMARY KEY.
            var ordered = snapshot.Constraints
                .OrderBy(c => c.IsInbound ? 0 : 1)
                .ThenBy(c => c.ConstraintType switch
                {
                    "f" => 1,
                    "c" => 2,
                    "u" => 3,
                    "p" => 4,
                    _ => 5
                })
                .ThenBy(c => c.ConstraintName, StringComparer.Ordinal)
                .ToList();

            await using var conn = await OpenAsync(connection, password, cancellationToken);
            foreach (var entry in ordered)
            {
                await ExecuteNonQueryAsync(conn, entry.DropSql, cancellationToken);
            }
        }

        public async Task RestoreAsync(Connection connection, string password, PostgresConstraintSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (IsMock(connection) || snapshot.Constraints.Count == 0) return;

            // Local PRIMARY / UNIQUE / CHECK / FK first, then inbound FKs.
            var ordered = snapshot.Constraints
                .OrderBy(c => c.IsInbound ? 1 : 0)
                .ThenBy(c => c.ConstraintType switch
                {
                    "p" => 1,
                    "u" => 2,
                    "c" => 3,
                    "f" => 4,
                    _ => 5
                })
                .ThenBy(c => c.ConstraintName, StringComparer.Ordinal)
                .ToList();

            await using var conn = await OpenAsync(connection, password, cancellationToken);
            foreach (var entry in ordered)
            {
                if (await ConstraintExistsAsync(conn, entry.OwnerSchema, entry.OwnerTable, entry.ConstraintName, cancellationToken))
                {
                    continue;
                }

                await ExecuteNonQueryAsync(conn, entry.CreateSql, cancellationToken);
            }
        }

        public async Task TruncateAsync(Connection connection, string password, string schema, string table, CancellationToken cancellationToken)
        {
            if (IsMock(connection)) return;

            await using var conn = await OpenAsync(connection, password, cancellationToken);
            var sql = $"TRUNCATE TABLE {SqlIdentifier.QuotePostgresQualified(schema, table)} RESTART IDENTITY CASCADE;";
            await ExecuteNonQueryAsync(conn, sql, cancellationToken);
        }

        // Shared with PostgresSchemaInspector so the mock short-circuit is defined once.
        private static bool IsMock(Connection connection) =>
            PostgresConnectionFactory.IsMock(connection);

        private static Task<NpgsqlConnection> OpenAsync(Connection connection, string password, CancellationToken cancellationToken) =>
            PostgresConnectionFactory.OpenAsync(connection, password, cancellationToken);

        private static async Task ExecuteNonQueryAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<bool> ConstraintExistsAsync(
            NpgsqlConnection conn,
            string ownerSchema,
            string ownerTable,
            string constraintName,
            CancellationToken cancellationToken)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT EXISTS (
    SELECT 1
    FROM pg_constraint c
    JOIN pg_class t ON t.oid = c.conrelid
    JOIN pg_namespace n ON n.oid = t.relnamespace
    WHERE n.nspname = @schema
      AND t.relname = @table
      AND c.conname = @name
);";
            cmd.Parameters.AddWithValue("schema", ownerSchema);
            cmd.Parameters.AddWithValue("table", ownerTable);
            cmd.Parameters.AddWithValue("name", constraintName);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is true;
        }
    }
}
