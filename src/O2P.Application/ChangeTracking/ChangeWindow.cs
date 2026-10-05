using System;

namespace O2P.Application.ChangeTracking
{
    /// <summary>
    /// The arithmetic that decides which part of Oracle's history a change copy looks at. Kept pure and
    /// separate because a mistake here loses updates silently - nothing downstream could notice.
    ///
    /// A transaction can change a row now and commit later. A copy that reads the row before the commit
    /// sees the old value, so the next copy must look at that change again or it is lost for good. The
    /// three reads are taken in a fixed order - S0 (current SCN), then the oldest open transaction's start
    /// (lw), then S1 (current SCN again) - and the resume point is min(S0 - 1, lw - 1). For any change at
    /// or before S1 that is still uncommitted at S1: if its transaction was already open when lw was read,
    /// lw is at or before its start; if it opened later, its start is after S0. Either way the change sits
    /// after the resume point, so the next copy mines it again. Re-reading it is harmless, because applying
    /// is by key and idempotent.
    /// </summary>
    public static class ChangeWindow
    {
        /// <summary>Everything at or before this SCN is known to be in the destination once the copy succeeds.</summary>
        public static decimal ResumePoint(decimal s0, decimal? oldestOpenStart) =>
            oldestOpenStart == null ? s0 - 1 : Math.Min(s0 - 1, oldestOpenStart.Value - 1);

        /// <summary>
        /// The lowest point any of the tables still needs, which is where one shared mining pass starts.
        /// Rows at or before a given table's own point are ignored for that table.
        /// </summary>
        public static decimal MiningStart(System.Collections.Generic.IEnumerable<decimal> fromScns)
        {
            decimal? lowest = null;
            foreach (var scn in fromScns)
            {
                if (lowest == null || scn < lowest) lowest = scn;
            }

            return lowest ?? throw new ArgumentException("No tables to mine.", nameof(fromScns));
        }
    }
}
