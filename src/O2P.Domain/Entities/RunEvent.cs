using System;

namespace O2P.Domain.Entities
{
    public class RunEvent
    {
        public long Id { get; set; }
        public long? JobRunId { get; set; }
        public string Actor { get; set; } = "system";
        public string Event { get; set; } = null!;
        public string? DetailJson { get; set; }
        public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    }
}
