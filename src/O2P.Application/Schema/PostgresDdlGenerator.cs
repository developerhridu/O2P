using O2P.Domain.Entities;
using System.Linq;
using System.Text;

namespace O2P.Application.Schema
{
    public static class PostgresDdlGenerator
    {
        /// <param name="style">
        /// Which spelling to give the new table's columns. O2P creates tables in lower case, so this is
        /// <see cref="PostgresName.LowerStyle"/> in practice; the parameter exists so the one resolver
        /// decides every destination name, here as well as in the comparer and the writer.
        /// </param>
        public static string GenerateTableDdl(ManifestTable table, string targetSchema, string targetTableName, string? style = PostgresName.SourceStyle)
        {
            var sb = new StringBuilder();
            // No IF NOT EXISTS: this only runs once the engine has observed the table to be absent,
            // so the clause could only mask a race with a concurrent job, or the name resolving to a
            // view or foreign table (TableExistsAsync filters relkind to 'r'/'p'). Either should fail
            // that one table loudly with 42P07; a retry then takes the reuse path and gets checked.
            sb.AppendLine($"CREATE TABLE {SqlIdentifier.QuotePostgresQualified(targetSchema, targetTableName)} (");

            var includedColumns = table.Columns.Where(c => !c.IsExcluded).ToList();
            
            for (int i = 0; i < includedColumns.Count; i++)
            {
                var col = includedColumns[i];
                var nullability = col.IsNullable ? "NULL" : "NOT NULL";
                
                // Quote identifiers to preserve case. A lower-case name quoted is the same object as
                // one written bare, so this stays safe and the table is still queryable unquoted.
                sb.Append($"    {SqlIdentifier.QuotePostgres(PostgresName.TargetColumn(col, style))} {col.PostgresDataType} {nullability}");
                
                if (i < includedColumns.Count - 1)
                {
                    sb.AppendLine(",");
                }
                else
                {
                    sb.AppendLine();
                }
            }

            sb.AppendLine(");");
            return sb.ToString();
        }

        /// <remarks>
        /// Nothing calls this today - the engine only creates the table - so no key or index is made in
        /// the destination. It follows the same naming rule anyway, so that wiring it up later cannot
        /// quietly bring upper-case names back.
        /// </remarks>
        public static string GenerateConstraintsAndIndexesDdl(ManifestTable table, string targetSchema, string targetTableName, string? style = PostgresName.SourceStyle)
        {
            var sb = new StringBuilder();

            // Primary Key
            var pkColumns = table.Columns.Where(c => !c.IsExcluded && c.IsPrimaryKey).ToList();
            if (pkColumns.Any())
            {
                var pkColsStr = string.Join(", ", pkColumns.Select(c => SqlIdentifier.QuotePostgres(PostgresName.TargetColumn(c, style))));
                var constraintName = SqlIdentifier.QuotePostgres(MaybeLower($"pk_{targetTableName}", style));
                sb.AppendLine($"ALTER TABLE {SqlIdentifier.QuotePostgresQualified(targetSchema, targetTableName)} ADD CONSTRAINT {constraintName} PRIMARY KEY ({pkColsStr});");
            }

            // Indexes
            foreach (var idx in table.Indexes)
            {
                var unique = idx.IsUnique ? "UNIQUE " : "";
                // M3 simplified comma-separated string for columns
                var cols = string.Join(", ", idx.Columns.Split(',').Select(c => SqlIdentifier.QuotePostgres(MaybeLower(c.Trim(), style))));
                var indexName = SqlIdentifier.QuotePostgres(MaybeLower($"idx_{targetTableName}_{idx.IndexName}", style));
                sb.AppendLine($"CREATE {unique}INDEX {indexName} ON {SqlIdentifier.QuotePostgresQualified(targetSchema, targetTableName)} ({cols});");
            }

            return sb.ToString();
        }

        private static string MaybeLower(string name, string? style) =>
            PostgresName.IsLower(style) ? PostgresName.For(name) : name;
    }
}
