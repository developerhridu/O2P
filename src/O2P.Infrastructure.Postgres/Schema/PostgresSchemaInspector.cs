using Npgsql;
using NpgsqlTypes;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Schema
{
    public class PostgresSchemaInspector : IPostgresSchemaInspector
    {
        public async Task<IReadOnlyList<PostgresLiveColumn>?> GetTableColumnsAsync(
            Connection connection,
            string password,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            // Null, not empty: an empty list would read as "the table has no columns" and report
            // every manifest column as missing.
            if (PostgresConnectionFactory.IsMock(connection)) return null;

            await using var conn = await PostgresConnectionFactory.OpenAsync(connection, password, cancellationToken);
            await using var cmd = conn.CreateCommand();

            // pg_attribute rather than information_schema.columns: that view is privilege-filtered,
            // so a column the migration user holds no privilege on simply vanishes - which would both
            // hide a blocking NOT NULL column and report real columns as missing. pg_catalog is
            // unfiltered, and format_type gives one canonical string ("integer", never "int4").
            //
            // attidentity/attgenerated are load-bearing: atthasdef is false for
            // GENERATED ALWAYS AS IDENTITY, so atthasdef alone would flag those as unpopulatable.
            // Both need PG 12+; preflight already requires 14+.
            cmd.CommandText = @"
SELECT a.attname,
       format_type(a.atttypid, a.atttypmod) AS formatted_type,
       a.attnotnull,
       a.atthasdef,
       a.attidentity  <> '' AS is_identity,
       a.attgenerated <> '' AS is_generated
FROM pg_attribute a
JOIN pg_class     c ON c.oid = a.attrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = @schema
  AND c.relname = @table
  AND c.relkind IN ('r', 'p')
  AND a.attnum > 0
  AND NOT a.attisdropped
ORDER BY a.attnum;";
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);

            var columns = new List<PostgresLiveColumn>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new PostgresLiveColumn(
                    ColumnName: reader.GetString(0),
                    FormattedType: reader.GetString(1),
                    NotNull: reader.GetBoolean(2),
                    HasDefault: reader.GetBoolean(3),
                    IsIdentity: reader.GetBoolean(4),
                    IsGenerated: reader.GetBoolean(5)));
            }

            // An empty list is returned as-is, not as null: the engine only asks once it has seen the
            // table exist, so "no columns" is a real answer (a zero-column table, or one dropped from
            // under us). Letting the comparer report every column as missing fails cleanly, without
            // touching anything, which beats skipping the check and hitting a raw COPY error later.
            return columns;
        }

        public async Task<IReadOnlyList<TargetSchema>> ListSchemasAsync(
            Connection connection,
            string password,
            CancellationToken cancellationToken)
        {
            if (PostgresConnectionFactory.IsMock(connection))
            {
                return new[]
                {
                    new TargetSchema("public", true),
                    new TargetSchema("reporting", true),
                    new TargetSchema("readonly_archive", false),
                };
            }

            await using var conn = await PostgresConnectionFactory.OpenAsync(connection, password, cancellationToken);
            await using var cmd = conn.CreateCommand();

            // USAGE is the minimum a run needs; without it nothing in the schema is reachable, so
            // offering it would only produce a readiness-check failure later. CREATE is reported
            // rather than required, because a run into existing tables does not need it.
            cmd.CommandText = @"
SELECT n.nspname,
       has_schema_privilege(n.nspname, 'CREATE') AS can_create
FROM pg_namespace n
WHERE n.nspname NOT LIKE 'pg\_%'
  AND n.nspname <> 'information_schema'
  AND has_schema_privilege(n.nspname, 'USAGE')
ORDER BY n.nspname;";

            var schemas = new List<TargetSchema>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                schemas.Add(new TargetSchema(reader.GetString(0), reader.GetBoolean(1)));
            }

            return schemas;
        }

        public async Task<IReadOnlyCollection<string>> GetExistingTableNamesAsync(
            Connection connection,
            string password,
            string schema,
            IReadOnlyCollection<string> tableNames,
            CancellationToken cancellationToken)
        {
            if (PostgresConnectionFactory.IsMock(connection) || tableNames.Count == 0)
            {
                return new List<string>();
            }

            await using var conn = await PostgresConnectionFactory.OpenAsync(connection, password, cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT c.relname
FROM pg_class     c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = @schema
  AND c.relkind IN ('r', 'p')
  AND c.relname = ANY(@names);";
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = tableNames.Distinct().ToArray()
            });

            var found = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add(reader.GetString(0));
            }

            return found;
        }
    }
}
