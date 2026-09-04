using System;

namespace O2P.Domain.Entities
{
    public class DiscoveryCache
    {
        public long Id { get; set; }
        public long ConnectionId { get; set; }
        public Connection Connection { get; set; } = null!;
        
        public string Owner { get; set; } = null!;
        public string TableName { get; set; } = null!;
        
        public long? NumRows { get; set; }
        public long? SegmentBytes { get; set; }
        public long? LobBytes { get; set; }
        public bool IsPartitioned { get; set; }
        public bool IsIot { get; set; }
        
        public DateTimeOffset LastRefreshedAt { get; set; }
        
        public ICollection<DiscoveryColumnCache> Columns { get; set; } = new List<DiscoveryColumnCache>();
    }
}
