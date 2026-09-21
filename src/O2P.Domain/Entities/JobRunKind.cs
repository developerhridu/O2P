namespace O2P.Domain.Entities
{
    /// <summary>The two kinds of run. Stored as text on <see cref="JobRun.Kind"/>.</summary>
    public static class JobRunKind
    {
        /// <summary>Copies whole tables: creates a missing destination table, empties an existing one.</summary>
        public const string Bulk = "bulk";

        /// <summary>
        /// Copies only what changed in Oracle since the last copy. It never creates, empties or restores
        /// constraints on a table - every path that does is guarded against this kind.
        /// </summary>
        public const string Changes = "changes";

        public static bool IsChanges(string? kind) => kind == Changes;
    }
}
