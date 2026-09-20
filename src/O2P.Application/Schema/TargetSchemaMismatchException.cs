using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace O2P.Application.Schema
{
    /// <summary>
    /// Thrown when a pre-existing target table cannot accept the manifest's columns. Raised before
    /// anything is snapshotted, dropped, truncated or loaded, so the operator's table and its rows
    /// are left exactly as they were.
    /// </summary>
    public sealed class TargetSchemaMismatchException : Exception
    {
        // The whole message lands in TableRun.ErrorMessage and is rendered in the UI. A table with
        // a hundred wrong columns is not more actionable than one with twenty.
        private const int MaxListedProblems = 20;

        public string SchemaName { get; }
        public string TableName { get; }
        public IReadOnlyList<SchemaMismatch> Problems { get; }

        public TargetSchemaMismatchException(string schemaName, string tableName, IReadOnlyList<SchemaMismatch> problems)
            : base(BuildMessage(schemaName, tableName, problems))
        {
            SchemaName = schemaName;
            TableName = tableName;
            Problems = problems;
        }

        private static string BuildMessage(string schemaName, string tableName, IReadOnlyList<SchemaMismatch> problems)
        {
            var blocking = problems.Where(p => p.Severity == MismatchSeverity.Fail).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Target table \"{schemaName}\".\"{tableName}\" already exists, but its columns do not match the tables you selected.");
            sb.AppendLine($"Nothing in it was changed, emptied or copied into. {blocking.Count} incompatibilit{(blocking.Count == 1 ? "y" : "ies")}:");

            foreach (var problem in blocking.Take(MaxListedProblems))
            {
                sb.AppendLine($"  - {problem.Message}");
            }

            if (blocking.Count > MaxListedProblems)
            {
                sb.AppendLine($"  ...and {blocking.Count - MaxListedProblems} more.");
            }

            sb.Append("Fix the destination table, or remove those columns from the table selection, then use \"Retry failed\".");
            return sb.ToString();
        }
    }
}
