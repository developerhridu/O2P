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

        public long? ManifestTableId { get; set; }
        public long? TrackedTableId { get; set; }
        public string? SourceOwner { get; set; }
        public string? SourceTable { get; set; }
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

        /// <summary>
        /// Bulk runs: the Oracle SCN change tracking would continue from, recorded before the first batch
        /// read a row - min(S0 - 1, oldest open transaction start - 1), so nothing changed during the load
        /// is missed. Null when it could not be read (missing privilege, mock source); such a copy cannot
        /// be tracked, and the bulk copy itself is unaffected.
        /// </summary>
        public decimal? SourceStartScn { get; set; }

        /// <summary>
        /// Whether ARCHIVELOG and the supplemental logging change tracking needs were already on when this
        /// bulk copy started. Redo written before they were on carries no keys, so a copy started without
        /// them cannot be tracked.
        /// </summary>
        public bool? LoggingReadyAtStart { get; set; }

        /// <summary>Oracle object and partition ids at the start of a bulk copy, as JSON; see TrackedTable.</summary>
        public string? SourceObjectIdsJson { get; set; }

        /// <summary>The Oracle primary key at the start of a bulk copy, as JSON; null when it has none usable.</summary>
        public string? SourceKeyJson { get; set; }

        // Change runs: what applying the changes did.
        public long RowsWritten { get; set; } = 0;
        public long RowsDeleted { get; set; } = 0;

        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        // Metrics
        public long RowsMigrated { get; set; } = 0;
        public long BytesMigrated { get; set; } = 0;

        public ICollection<ChunkLog> Chunks { get; set; } = new List<ChunkLog>();
    }
}
