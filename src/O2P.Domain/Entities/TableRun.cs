using System;
using System.Collections.Generic;

namespace O2P.Domain.Entities
{
    public class TableRun
    {
        public long Id { get; set; }
        public long JobRunId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public JobRun? JobRun { get; set; }

        public long ManifestTableId { get; set; }
        public ManifestTable ManifestTable { get; set; } = null!;

        public string Status { get; set; } = "Pending"; // Pending | Creating | Chunking | Loading | Indexing | Validating | Done | Failed

        public string TargetTableName { get; set; } = null!; // The allocated name e.g. MY_TABLE_mg1
        
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// JSON snapshot of Postgres constraints suspended for load (restored after load or on failure).
        /// </summary>
        public string? ConstraintSnapshotJson { get; set; }

        /// <summary>
        /// True when the target table already existed and was reused, false when O2P created it.
        /// Null for runs recorded before this was tracked.
        /// </summary>
        public bool? TargetTablePreExisted { get; set; }

        /// <summary>
        /// Which spelling this run uses for destination column names: "lower" for a table O2P created
        /// (or found already in lower case), "source" for one an earlier run left behind under Oracle's
        /// own upper-case spelling. Resolved once when the table is prepared and frozen for the whole
        /// run, so every batch agrees. Null on runs recorded before this was tracked, read as "source".
        /// </summary>
        public string? TargetNameStyle { get; set; }

        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        // Metrics
        public long RowsMigrated { get; set; } = 0;
        public long BytesMigrated { get; set; } = 0;

        public ICollection<ChunkLog> Chunks { get; set; } = new List<ChunkLog>();
    }
}
