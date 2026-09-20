using System;

namespace O2P.Domain.Entities
{
    /// <summary>
    /// One row per running Worker process, refreshed every few seconds. The API reads these to tell
    /// the user whether anything is actually processing runs. Without it a queued run just sits at
    /// "Waiting" with no hint that the program that would start it is not running.
    /// </summary>
    public class WorkerHeartbeat
    {
        public long Id { get; set; }

        /// <summary>Unique per process start, so a restarted Worker is a new row, not a resurrection.</summary>
        public string InstanceId { get; set; } = null!;

        public string Host { get; set; } = null!;
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>Last time this Worker reported in. A row that stops advancing is a dead Worker.</summary>
        public DateTimeOffset LastSeenAt { get; set; }
    }
}
