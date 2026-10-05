using System;

namespace O2P.Application.Copying
{
    /// <summary>
    /// How many batches a table is cut into. It used to be a fixed 16 whatever the size, which for a
    /// 25-million-row LOB table meant batches of 1.5 million rows lasting 15-20 minutes each - long enough
    /// for a VPN to drop them and for the old 20-minute limit to stop them, losing all their work.
    /// </summary>
    public static class BatchPlan
    {
        /// <param name="estimatedRows">Oracle's statistics, or an exact count; null when unknown.</param>
        public static int CountFor(long? estimatedRows, bool hasLobs, CopyTuningOptions options)
        {
            if (estimatedRows is null or < 0) return Math.Max(1, options.BatchesWhenSizeUnknown);

            var perBatch = Math.Max(1, hasLobs ? options.RowsPerLobBatch : options.RowsPerBatch);
            var batches = (long)Math.Ceiling(estimatedRows.Value / (double)perBatch);
            return (int)Math.Clamp(batches, 1, Math.Max(1, options.MaxBatchesPerTable));
        }

        /// <summary>
        /// Wait before a batch's next attempt: 30 s, 1 min, 2 min, then 5 min. Long enough for a dropped
        /// VPN or a restarting database to come back, short enough not to leave a run idling.
        /// </summary>
        public static TimeSpan RetryDelay(int attemptJustFailed) => attemptJustFailed switch
        {
            <= 1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(1),
            3 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };
    }
}
