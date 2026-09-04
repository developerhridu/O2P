namespace O2P.Domain.Entities
{
    public class ManifestTable
    {
        public long Id { get; set; }
        public long ManifestId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public Manifest? Manifest { get; set; }

        public string Owner { get; set; } = null!;
        public string TableName { get; set; } = null!;
        public bool Included { get; set; }
        public string? WhereClause { get; set; }
        public string[]? ExcludedColumns { get; set; } // Legacy field from M1, can be kept or removed
        
        public ICollection<ManifestColumn> Columns { get; set; } = new List<ManifestColumn>();
        public ICollection<ManifestIndex> Indexes { get; set; } = new List<ManifestIndex>();
        
        // Snapshot stats
        public long? EstRows { get; set; }
        public long? EstBytes { get; set; }
        public bool HasLobs { get; set; }
        public bool IsPartitioned { get; set; }
        public bool IsIot { get; set; }
    }
}
