using System;

namespace O2P.Domain.Entities
{
    public class RunLog
    {
        public long Id { get; set; }
        public long? JobRunId { get; set; }
        public long? TableRunId { get; set; }
        public long? ChunkLogId { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public string Level { get; set; } = "Information";
        public string Source { get; set; } = "system";
        public string Message { get; set; } = null!;
        public string? PropertiesJson { get; set; }
    }
}
