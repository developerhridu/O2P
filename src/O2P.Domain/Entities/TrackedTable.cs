using System;

namespace O2P.Domain.Entities
{
    /// <summary>
    /// One destination table kept in step with its Oracle source by "Copy changes".
    ///
    /// Deliberately linked to nothing by foreign key. Saving a table selection deletes and re-adds its
    /// tables, and table runs hang off those rows with ON DELETE CASCADE, so a tracker attached to either
    /// would vanish on an ordinary save. It holds instead a frozen copy of exactly what the bulk copy did,
    /// and it is keyed by what is actually being kept in sync: the destination table.
    /// </summary>
    public class TrackedTable
    {
        public long Id { get; set; }

        // ---- source ---------------------------------------------------------------------------
        public long SourceConnectionId { get; set; }
        public string SourceOwner { get; set; } = null!;
        public string SourceTable { get; set; } = null!;

        /// <summary>
        /// The copied columns in copy order - name, Oracle type, PostgreSQL type, nullability - exactly as
        /// the bulk copy wrote them. Change runs read and write these, not whatever the selection holds now.
        /// </summary>
        public string ColumnsJson { get; set; } = "[]";

        /// <summary>The bulk copy's row filter. Change runs apply it too, or rows it skipped would arrive.</summary>
        public string? WhereClause { get; set; }

        /// <summary>The Oracle primary key columns and types, in key order.</summary>
        public string KeyColumnsJson { get; set; } = "[]";

        /// <summary>
        /// Oracle object and data-object ids of the table and its partitions. A change means the table was
        /// truncated, moved, redefined or exchanged, which the redo cannot describe.
        /// </summary>
        public string ObjectIdsJson { get; set; } = "{}";

        // ---- destination ----------------------------------------------------------------------
        public long TargetConnectionId { get; set; }
        public string TargetSchema { get; set; } = null!;
        public string TargetTableName { get; set; } = null!;

        /// <summary>"lower" or "source", as resolved by the bulk copy; decides the column spelling.</summary>
        public string? TargetNameStyle { get; set; }

        // ---- state ----------------------------------------------------------------------------
        /// <summary>The SCN everything before which is already in the destination. Never moves back on its own.</summary>
        public decimal? LastScn { get; set; }

        /// <summary>See <see cref="TrackedTableStatus"/>.</summary>
        public string Status { get; set; } = TrackedTableStatus.NeedsFirstSync;

        /// <summary>The change run using this tracker now; a bulk copy of the same table waits for it.</summary>
        public long? ActiveJobRunId { get; set; }

        /// <summary>The open Oracle transactions that held the resume point back last time, as JSON.</summary>
        public string? HeldBackByJson { get; set; }

        /// <summary>The bulk table run it was set up from. Informational only - no foreign key, see above.</summary>
        public long? SetUpFromTableRunId { get; set; }

        public DateTimeOffset? LastSyncedAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public static class TrackedTableStatus
    {
        /// <summary>
        /// Set up from a bulk copy of a table O2P created, which has no key yet. The first "Copy changes"
        /// settles it - removes any row the fuzzy load copied twice - and only then adds the primary key.
        /// </summary>
        public const string NeedsFirstSync = "needs_first_sync";

        /// <summary>Up to date as of <see cref="TrackedTable.LastScn"/>, and ready for the next press.</summary>
        public const string Ready = "ready";

        /// <summary>The history can no longer describe what happened (truncate, missing logs, ...). Only a new bulk copy helps.</summary>
        public const string NeedsBulkCopy = "needs_bulk_copy";
    }
}
