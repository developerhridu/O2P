using System;
using System.Collections.Generic;

namespace O2P.Domain.Entities
{
    public class Manifest
    {
        public long Id { get; set; }
        public long ApplicationId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public Application? Application { get; set; }

        public string Name { get; set; } = null!;
        public int Version { get; set; }
        public string? Notes { get; set; }
        public string? CreatedBy { get; set; }
        
        public DateTimeOffset CreatedAt { get; set; }

        public ICollection<ManifestTable> Tables { get; set; } = new List<ManifestTable>();
    }
}
