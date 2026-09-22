using System;

namespace O2P.Domain.Entities
{
    /// <summary>
    /// The last exact row count of one table, in either database, for the Dashboard's row-count comparison.
    ///
    /// Kept apart from DiscoveryCache on purpose: a row there means "this table was scanned and can go into a
    /// migration", and generating a migration takes every cached table of the schema. Listing a schema here
    /// must not quietly add unscanned, column-less tables to the next migration.
    ///
    /// A row exists for every table the last table-list refresh found; Rows stays null until it is counted.
    /// No relationship to Connection, like TrackedTable: deleting a database removes its rows explicitly.
    /// </summary>
    public class TableRowCount
    {
        public long Id { get; set; }

        public long ConnectionId { get; set; }

        /// <summary>Oracle owner or PostgreSQL schema, exactly as the database stores it.</summary>
        public string SchemaName { get; set; } = null!;

        /// <summary>Exactly as the database stores it.</summary>
        public string TableName { get; set; } = null!;

        /// <summary>Exact COUNT(*) of the whole table; null until counted, or when the last count failed.</summary>
        public long? Rows { get; set; }

        public DateTimeOffset? CountedAt { get; set; }

        /// <summary>How long the last count took, so a slow table can be recognised before counting it again.</summary>
        public long? DurationMs { get; set; }

        /// <summary>Why the last count failed; null when it succeeded. The previous Rows is kept.</summary>
        public string? Error { get; set; }

        /// <summary>When a table-list refresh last saw this table.</summary>
        public DateTimeOffset ListedAt { get; set; }
    }
}
