namespace O2P.Application.Copying
{
    /// <summary>
    /// How a bulk copy is cut up, watched and retried. Bound from the Worker's configuration
    /// ("Copying" section); the defaults come from what a real run over a VPN showed - see
    /// docs/Ops_Runbook.md, "Bulk copy speed".
    /// </summary>
    public sealed class CopyTuningOptions
    {
        /// <summary>
        /// Rows per batch for an ordinary table. Small enough that a batch lasts a minute or two, so a
        /// dropped connection costs little and progress shows; large enough that per-batch overhead is noise.
        /// </summary>
        public int RowsPerBatch { get; set; } = 200_000;

        /// <summary>Rows per batch for a table with LOB columns, whose rows can be hundreds of times larger.</summary>
        public int RowsPerLobBatch { get; set; } = 25_000;

        /// <summary>Upper bound on batches per table, so a huge table cannot flood the batch list.</summary>
        public int MaxBatchesPerTable { get; set; } = 2_000;

        /// <summary>Batches for a table whose size is unknown (no statistics and never counted).</summary>
        public int BatchesWhenSizeUnknown { get; set; } = 16;

        /// <summary>
        /// A batch is stopped only when no row has moved for this long. Replaces the old fixed
        /// 20-minute limit, which stopped batches that were still copying and threw their work away.
        /// </summary>
        public int StallMinutes { get; set; } = 10;

        /// <summary>Optional absolute limit on one batch, in minutes. 0 = none (the stall watchdog is enough).</summary>
        public int MaxBatchMinutes { get; set; } = 0;

        /// <summary>
        /// Attempts per batch when it fails for a reason that can pass - a dropped or refused connection,
        /// a stall. A data error (bad value, constraint) is never retried: repeating it cannot help.
        /// </summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>Global cap on rows read per second across the Worker. 0 = no cap.</summary>
        public int MaxRowsPerSecond { get; set; } = 0;

        /// <summary>
        /// Bytes of each LOB fetched together with its row. Anything beyond it costs a separate round trip,
        /// which over a VPN is the expensive part. 256 KB covers typical images while still letting
        /// <see cref="RowsPerRoundTrip"/> rows fit in <see cref="MaxFetchBytes"/>: the driver reserves this
        /// much per LOB column per row, so a larger value means fewer rows per round trip.
        /// </summary>
        public int InitialLobFetchSize { get; set; } = 256 * 1024;

        /// <summary>
        /// Rows the reader asks Oracle for per network round trip. The fetch buffer is sized from the real
        /// row size to hold this many; it used to be a fixed 1 MB for LOB tables - about 16 rows a trip.
        /// </summary>
        public int RowsPerRoundTrip { get; set; } = 100;

        /// <summary>Largest fetch buffer per reading session, which bounds memory when rows are wide.</summary>
        public int MaxFetchBytes { get; set; } = 32 * 1024 * 1024;
    }
}
