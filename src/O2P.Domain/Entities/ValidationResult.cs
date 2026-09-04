using System;

namespace O2P.Domain.Entities
{
    public class ValidationResult
    {
        public long Id { get; set; }

        public long TableRunId { get; set; }
        public TableRun TableRun { get; set; } = null!;

        public string CheckKind { get; set; } = string.Empty; // 'rowcount', 'null_count', 'min_max', etc.
        public string? ColumnName { get; set; }

        public string SourceValue { get; set; } = string.Empty;
        public string TargetValue { get; set; } = string.Empty;
        public bool Passed { get; set; }

        public string? DetailJson { get; set; }
    }
}
