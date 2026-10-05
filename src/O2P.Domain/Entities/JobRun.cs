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

        /// <summary>
        /// "bulk" copies whole tables (and may empty and create them); "changes" copies only what changed
        /// in Oracle since the last copy and must never reach a path that empties or creates a table.
        /// See <see cref="JobRunKind"/>.
        /// </summary>
        public string Kind { get; set; } = JobRunKind.Bulk;

        /// <summary>The copier currently running a change run, and how long its claim lasts.</summary>
        public string? WorkerId { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }

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
