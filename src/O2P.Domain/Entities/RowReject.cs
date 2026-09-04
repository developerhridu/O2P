using System;

namespace O2P.Domain.Entities
{
    public class RowReject
    {
        public long Id { get; set; }
        public long TableRunId { get; set; }
        public long? ChunkLogId { get; set; }
        public string? SourceRowId { get; set; }
        public string Reason { get; set; } = null!;
        public string? ColumnName { get; set; }
        public string? PayloadJson { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
