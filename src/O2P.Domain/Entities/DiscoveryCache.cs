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
        
        /// <summary>
        /// Oracle's row count, or null when it does not know one. ALL_TABLES.NUM_ROWS is populated
        /// only after DBMS_STATS has gathered statistics, so null means "unknown" - never zero.
        /// </summary>
        public long? NumRows { get; set; }

        /// <summary>Table size in bytes, or null when no source for it was readable.</summary>
        public long? SegmentBytes { get; set; }
        public long? LobBytes { get; set; }
        public bool IsPartitioned { get; set; }
        public bool IsIot { get; set; }

        /// <summary>
        /// True when <see cref="SegmentBytes"/> was estimated from statistics rather than read from a
        /// segments view, so the UI can label it instead of presenting it as fact.
        /// </summary>
        public bool SizeIsEstimate { get; set; }

        /// <summary>
        /// When <see cref="NumRows"/> came from a real COUNT(*). Null means it came from Oracle's
        /// statistics, or is unknown.
        /// </summary>
        public DateTimeOffset? RowsCountedAt { get; set; }

        public DateTimeOffset LastRefreshedAt { get; set; }
        
        public ICollection<DiscoveryColumnCache> Columns { get; set; } = new List<DiscoveryColumnCache>();
    }
}
