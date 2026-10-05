using System;
using System.Diagnostics;
using System.Threading;

namespace O2P.Application.Copying
{
    /// <summary>
    /// What one batch has done so far, shared by its reader and writer and watched by the Worker. Lets the
    /// Worker show live progress, stop a batch only when it has genuinely stalled, and say afterwards which
    /// side was slow. Thread-safe and allocation-free per row.
    /// </summary>
    public sealed class ChunkProgress
    {
        private long _rows;
        private long _bytes;
        private long _lastProgressTimestamp = Stopwatch.GetTimestamp();
        private long _waitingForSourceTicks;
        private long _waitingForDestinationTicks;

        public long Rows => Interlocked.Read(ref _rows);

        /// <summary>Approximate: bytes for binary, characters for text, fixed sizes for numbers and dates.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        /// <summary>How long since the reader or the writer last moved a row.</summary>
        public TimeSpan SinceLastProgress =>
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgressTimestamp));

        /// <summary>Time the reader spent waiting for Oracle to hand over the next row.</summary>
        public TimeSpan WaitingForSource => TimeSpan.FromTicks(Interlocked.Read(ref _waitingForSourceTicks));

        /// <summary>Time the reader spent blocked because the writer (PostgreSQL) had not taken rows yet.</summary>
        public TimeSpan WaitingForDestination => TimeSpan.FromTicks(Interlocked.Read(ref _waitingForDestinationTicks));

        /// <summary>A row came off the source.</summary>
        public void RowRead(long sourceWaitTimestampStart, long destinationWaitTimestampStart, long now)
        {
            Interlocked.Add(ref _waitingForSourceTicks, Stopwatch.GetElapsedTime(sourceWaitTimestampStart, destinationWaitTimestampStart).Ticks);
            Interlocked.Add(ref _waitingForDestinationTicks, Stopwatch.GetElapsedTime(destinationWaitTimestampStart, now).Ticks);
            Interlocked.Exchange(ref _lastProgressTimestamp, now);
        }

        /// <summary>A row went into the destination.</summary>
        public void RowWritten(long approximateBytes)
        {
            Interlocked.Increment(ref _rows);
            Interlocked.Add(ref _bytes, approximateBytes);
            Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
        }

        /// <summary>Approximate size of one value, as written.</summary>
        public static long SizeOf(object? value) => value switch
        {
            null or DBNull => 0,
            byte[] b => b.Length,
            string s => s.Length,
            char[] c => c.Length,
            decimal => 16,
            DateTime or DateTimeOffset or TimeSpan or long or double => 8,
            int or float => 4,
            short => 2,
            bool or byte => 1,
            _ => 8
        };
    }
}
