using O2P.Domain.Entities;
using System.Linq;
using System.Text;

namespace O2P.Application.Schema
{
    public static class PostgresDdlGenerator
    {
        public static string GenerateTableDdl(ManifestTable table, string targetSchema, string targetTableName)
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
                
                // Quote identifiers to preserve case
                sb.Append($"    {SqlIdentifier.QuotePostgres(col.ColumnName)} {col.PostgresDataType} {nullability}");
                
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

        public static string GenerateConstraintsAndIndexesDdl(ManifestTable table, string targetSchema, string targetTableName)
        {
            var sb = new StringBuilder();

            // Primary Key
            var pkColumns = table.Columns.Where(c => !c.IsExcluded && c.IsPrimaryKey).ToList();
            if (pkColumns.Any())
            {
                var pkColsStr = string.Join(", ", pkColumns.Select(c => SqlIdentifier.QuotePostgres(c.ColumnName)));
                var constraintName = SqlIdentifier.QuotePostgres($"pk_{targetTableName}");
                sb.AppendLine($"ALTER TABLE {SqlIdentifier.QuotePostgresQualified(targetSchema, targetTableName)} ADD CONSTRAINT {constraintName} PRIMARY KEY ({pkColsStr});");
            }

            // Indexes
            foreach (var idx in table.Indexes)
            {
                var unique = idx.IsUnique ? "UNIQUE " : "";
                // M3 simplified comma-separated string for columns
                var cols = string.Join(", ", idx.Columns.Split(',').Select(c => SqlIdentifier.QuotePostgres(c.Trim())));
                var indexName = SqlIdentifier.QuotePostgres($"idx_{targetTableName}_{idx.IndexName}");
                sb.AppendLine($"CREATE {unique}INDEX {indexName} ON {SqlIdentifier.QuotePostgresQualified(targetSchema, targetTableName)} ({cols});");
            }

            return sb.ToString();
        }
    }
}
