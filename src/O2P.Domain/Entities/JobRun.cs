using System;
using System.Collections.Generic;

namespace O2P.Domain.Entities
{
    public class JobRun
    {
        public long Id { get; set; }
        public long ApplicationId { get; set; }
        public Application Application { get; set; } = null!;

        public long ManifestId { get; set; }
        public Manifest Manifest { get; set; } = null!;

        public string Status { get; set; } = "Pending"; // Pending | Running | Done | Failed | Cancelled
        
        // Settings snapshot
        public string SourceSlot { get; set; } = null!; // oracle_live or oracle_test
        public string TargetSlot { get; set; } = null!; // pg_live or pg_test
        public string TargetSchema { get; set; } = "public";
        
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        public ICollection<TableRun> TableRuns { get; set; } = new List<TableRun>();
    }
}
