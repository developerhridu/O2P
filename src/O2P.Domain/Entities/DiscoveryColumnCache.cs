using System;

namespace O2P.Domain.Entities
{
    public class DiscoveryColumnCache
    {
        public long Id { get; set; }
        public long DiscoveryCacheId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public DiscoveryCache? DiscoveryCache { get; set; }

        public string ColumnName { get; set; } = null!;
        public int ColumnId { get; set; }
        public string DataType { get; set; } = null!;
        public int? DataLength { get; set; }
        public int? DataPrecision { get; set; }
        public int? DataScale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
    }
}
