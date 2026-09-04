using System;

namespace O2P.Domain.Entities
{
    public class TargetNameAllocation
    {
        public long Id { get; set; }
        public long TargetConnectionId { get; set; }
        public string SchemaName { get; set; } = null!;
        public string BaseName { get; set; } = null!;
        public int SuffixNumber { get; set; }
        public long TableRunId { get; set; }
        public string Status { get; set; } = "reserved";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
