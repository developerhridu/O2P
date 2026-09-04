using System;

namespace O2P.Domain.Entities
{
    public class ChunkLog
    {
        public long Id { get; set; }
        public long TableRunId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public TableRun? TableRun { get; set; }

        public int ChunkIndex { get; set; }

        // How the reader must slice this chunk: "rowid" / "partition_rowid" (ROWID BETWEEN),
        // "pk_range" (BoundColumn range), "partition" (whole partition), or "single" (whole table).
        public string Strategy { get; set; } = string.Empty;

        // Physical ROWID bounds for rowid/partition_rowid strategies, or the numeric primary-key
        // bounds (as strings) for pk_range. The sentinels "MIN"/"MAX" mean "unbounded on this side".
        public string StartRowId { get; set; } = string.Empty;
        public string EndRowId { get; set; } = string.Empty;

        // Primary-key column the pk_range bounds apply to (null for the ROWID/partition strategies).
        public string? BoundColumn { get; set; }

        // Oracle partition this chunk is confined to, read via a PARTITION (...) clause
        // (null for non-partitioned heap/pk_range chunks).
        public string? PartitionName { get; set; }

        public string Status { get; set; } = "Pending"; // Pending | Running | Done | Failed

        public string? WorkerId { get; set; } // Identifies which worker process claimed this
        public DateTimeOffset? LeaseExpiresAt { get; set; } // Zombie fencing

        public int AttemptCount { get; set; } = 0;
        public string? ErrorMessage { get; set; }

        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        
        public long RowsMigrated { get; set; } = 0;
        public long BytesMigrated { get; set; } = 0;
    }
}
