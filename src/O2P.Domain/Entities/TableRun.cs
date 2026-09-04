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

        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        // Metrics
        public long RowsMigrated { get; set; } = 0;
        public long BytesMigrated { get; set; } = 0;

        public ICollection<ChunkLog> Chunks { get; set; } = new List<ChunkLog>();
    }
}
