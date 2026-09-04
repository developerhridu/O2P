using System;

namespace O2P.Domain.Entities
{
    public class MetricSample
    {
        public long Id { get; set; }

        public long JobId { get; set; }
        public long JobRunId { get; set; }
        public JobRun JobRun { get; set; } = null!;

        public long? TableRunId { get; set; }
        public TableRun? TableRun { get; set; }

        public DateTimeOffset Timestamp { get; set; }

        public double RowsPerSecond { get; set; }
        public double MbPerSecond { get; set; }

        public int ActiveChunkWorkers { get; set; }
        public int OracleSessions { get; set; }
    }
}
