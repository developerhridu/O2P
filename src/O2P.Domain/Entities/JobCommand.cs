using System;

namespace O2P.Domain.Entities
{
    public class JobCommand
    {
        public long Id { get; set; }
        
        public long JobRunId { get; set; }
        public JobRun JobRun { get; set; } = null!;

        public string Scope { get; set; } = string.Empty; // 'job', 'table_run', 'chunk'
        public string Command { get; set; } = string.Empty; // 'pause', 'resume', 'cancel', 'update_throttle'
        public string? Payload { get; set; }

        public string? IssuedBy { get; set; }
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset? ProcessedAt { get; set; }
    }
}
