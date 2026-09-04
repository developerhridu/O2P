using System;

namespace O2P.Domain.Entities
{
    public class ManifestColumn
    {
        public long Id { get; set; }
        public long ManifestTableId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public ManifestTable? ManifestTable { get; set; }

        public string ColumnName { get; set; } = null!;
        public string OracleDataType { get; set; } = null!;
        public string PostgresDataType { get; set; } = null!;
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        
        // Exclude this column from the migration?
        public bool IsExcluded { get; set; } = false;
    }
}
