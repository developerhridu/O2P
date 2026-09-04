using System;

namespace O2P.Domain.Entities
{
    public class ManifestIndex
    {
        public long Id { get; set; }
        public long ManifestTableId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public ManifestTable? ManifestTable { get; set; }

        public string IndexName { get; set; } = null!;
        public bool IsUnique { get; set; }
        public string Columns { get; set; } = null!; // comma separated for simplicity in M3
        public string IndexType { get; set; } = "NORMAL"; // NORMAL, BITMAP, etc.
    }
}
