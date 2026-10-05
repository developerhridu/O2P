using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using O2P.Application.Copying;
using Xunit;

namespace O2P.Application.Tests;

// Stand-ins for the driver exceptions: ChunkFailure recognises them by type name and properties, so the
// tests need neither driver.
internal sealed class OracleException : Exception
{
    public OracleException(int number) : base($"ORA-{number:00000}") => Number = number;
    public int Number { get; }
}

internal sealed class PostgresException : Exception
{
    public PostgresException(string sqlState) : base(sqlState) => SqlState = sqlState;
    public string SqlState { get; }
}

internal sealed class NpgsqlException : Exception
{
    public NpgsqlException(bool transient) : base("npgsql") => IsTransient = transient;
    public bool IsTransient { get; }
}

public class CopyingTests
{
    private static readonly CopyTuningOptions Defaults = new();

    // ---- BatchPlan.CountFor -------------------------------------------------

    [Theory]
    [InlineData(1_000_000L, false, 5)]      // 200,000 per batch
    [InlineData(1_000_001L, false, 6)]      // rounds up, never drops rows off the end
    [InlineData(1_000_000L, true, 40)]      // LOB tables: 25,000 per batch
    [InlineData(25_000_000L, true, 1_000)]  // the real table from the log: ~1,000 batches, not 16
    [InlineData(0L, false, 1)]              // empty table still gets one batch
    [InlineData(10L, true, 1)]
    public void CountFor_sizes_batches_from_the_row_estimate(long rows, bool hasLobs, int expected) =>
        Assert.Equal(expected, BatchPlan.CountFor(rows, hasLobs, Defaults));

    [Fact]
    public void CountFor_unknown_size_falls_back_to_the_old_fixed_count() =>
        Assert.Equal(16, BatchPlan.CountFor(null, hasLobs: true, Defaults));

    [Fact]
    public void CountFor_is_capped() =>
        Assert.Equal(2_000, BatchPlan.CountFor(10_000_000_000L, hasLobs: false, Defaults));

    [Fact]
    public void CountFor_follows_the_settings()
    {
        var options = new CopyTuningOptions { RowsPerBatch = 1_000, MaxBatchesPerTable = 50 };
        Assert.Equal(10, BatchPlan.CountFor(10_000, hasLobs: false, options));
        Assert.Equal(50, BatchPlan.CountFor(1_000_000, hasLobs: false, options));
    }

    [Fact]
    public void CountFor_survives_nonsense_settings()
    {
        var options = new CopyTuningOptions { RowsPerBatch = 0, RowsPerLobBatch = -5, MaxBatchesPerTable = 0, BatchesWhenSizeUnknown = 0 };
        Assert.Equal(1, BatchPlan.CountFor(1_000, hasLobs: false, options));
        Assert.Equal(1, BatchPlan.CountFor(null, hasLobs: false, options));
    }

    // ---- BatchPlan.RetryDelay -----------------------------------------------

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 300)]
    [InlineData(9, 300)]
    public void RetryDelay_grows_then_levels_off(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), BatchPlan.RetryDelay(attempt));

    // ---- ChunkFailure.IsWorthRetrying ---------------------------------------

    [Theory]
    [InlineData(3113)]
    [InlineData(3114)]
    [InlineData(3135)]
    [InlineData(12170)]
    [InlineData(12537)]
    [InlineData(12541)]
    [InlineData(12547)]
    [InlineData(12560)]
    [InlineData(12571)]
    [InlineData(50000)]
    public void Oracle_connection_errors_are_retried(int number) =>
        Assert.True(ChunkFailure.IsWorthRetrying(new OracleException(number), attempt: 1, stalled: false));

    [Theory]
    [InlineData(1722)]  // invalid number
    [InlineData(942)]   // table or view does not exist
    [InlineData(1031)]  // insufficient privileges
    [InlineData(1555)]  // snapshot too old - repeating the same long read would fail the same way
    public void Oracle_data_and_statement_errors_are_not_retried(int number) =>
        Assert.False(ChunkFailure.IsWorthRetrying(new OracleException(number), attempt: 1, stalled: false));

    [Fact]
    public void Object_no_longer_exists_is_retried_once_only()
    {
        Assert.True(ChunkFailure.IsWorthRetrying(new OracleException(8103), attempt: 1, stalled: false));
        Assert.False(ChunkFailure.IsWorthRetrying(new OracleException(8103), attempt: 2, stalled: false));
    }

    [Theory]
    [InlineData("08006")]  // connection failure
    [InlineData("08001")]
    [InlineData("57P01")]  // admin shutdown / terminated backend
    [InlineData("57P03")]
    [InlineData("53300")]  // too many connections
    public void Postgres_connection_errors_are_retried(string state) =>
        Assert.True(ChunkFailure.IsWorthRetrying(new PostgresException(state), attempt: 1, stalled: false));

    [Theory]
    [InlineData("22P02")]  // invalid text representation
    [InlineData("23505")]  // unique violation
    [InlineData("22001")]  // value too long
    [InlineData("42P01")]  // undefined table
    public void Postgres_data_errors_are_not_retried(string state) =>
        Assert.False(ChunkFailure.IsWorthRetrying(new PostgresException(state), attempt: 1, stalled: false));

    [Fact]
    public void Network_exceptions_are_retried_even_when_wrapped()
    {
        Assert.True(ChunkFailure.IsWorthRetrying(new IOException("x", new SocketException(10054)), 1, false));
        Assert.True(ChunkFailure.IsWorthRetrying(new InvalidOperationException("outer", new SocketException(10054)), 1, false));
        Assert.True(ChunkFailure.IsWorthRetrying(new TimeoutException(), 1, false));
        Assert.True(ChunkFailure.IsWorthRetrying(new NpgsqlException(transient: true), 1, false));
    }

    [Fact]
    public void Connection_failures_known_only_by_message_are_retried()
    {
        Assert.True(ChunkFailure.IsWorthRetrying(new Exception("Failed to connect to 172.16.10.58:6501"), 1, false));
        Assert.True(ChunkFailure.IsWorthRetrying(new Exception("ORA-12xxx: TNS:packet reader failure"), 1, false));
    }

    [Fact]
    public void A_stall_is_always_worth_another_try() =>
        Assert.True(ChunkFailure.IsWorthRetrying(new OperationCanceledException(), attempt: 4, stalled: true));

    [Fact]
    public void Other_errors_are_not_retried()
    {
        Assert.False(ChunkFailure.IsWorthRetrying(new InvalidCastException("bad value"), 1, false));
        Assert.False(ChunkFailure.IsWorthRetrying(new NpgsqlException(transient: false), 1, false));
        Assert.False(ChunkFailure.IsWorthRetrying(new OperationCanceledException(), 1, stalled: false));
    }

    // ---- ChunkProgress --------------------------------------------------------

    [Theory]
    [InlineData(null, 0L)]
    [InlineData("hello", 5L)]
    [InlineData(42, 4L)]
    [InlineData(42L, 8L)]
    [InlineData(true, 1L)]
    public void SizeOf_counts_each_type(object? value, long expected) =>
        Assert.Equal(expected, ChunkProgress.SizeOf(value));

    [Fact]
    public void SizeOf_counts_binary_and_fixed_width_values()
    {
        Assert.Equal(200 * 1024, ChunkProgress.SizeOf(new byte[200 * 1024]));
        Assert.Equal(16, ChunkProgress.SizeOf(1.5m));
        Assert.Equal(8, ChunkProgress.SizeOf(DateTime.UtcNow));
        Assert.Equal(0, ChunkProgress.SizeOf(DBNull.Value));
    }

    [Fact]
    public void Progress_adds_up_rows_bytes_and_where_the_time_went()
    {
        var progress = new ChunkProgress();
        var start = Stopwatch.GetTimestamp();
        var handOver = start + Stopwatch.Frequency * 3;   // 3 s waiting for the source
        var done = handOver + Stopwatch.Frequency;         // 1 s waiting for the destination

        progress.RowRead(start, handOver, done);
        progress.RowWritten(100);
        progress.RowWritten(50);

        Assert.Equal(2, progress.Rows);
        Assert.Equal(150, progress.Bytes);
        Assert.Equal(3, progress.WaitingForSource.TotalSeconds, precision: 3);
        Assert.Equal(1, progress.WaitingForDestination.TotalSeconds, precision: 3);
        Assert.True(progress.SinceLastProgress < TimeSpan.FromSeconds(5));
    }
}
