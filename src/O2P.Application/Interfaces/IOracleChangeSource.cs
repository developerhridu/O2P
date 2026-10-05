using O2P.Application.ChangeTracking;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    /// <summary>One column of an Oracle primary key, in key order.</summary>
    public sealed record OracleKeyColumn(string Name, string DataType);

    /// <summary>
    /// What a bulk copy records about its source before the first batch reads, so change tracking can
    /// continue from it. Every field is best effort: a missing privilege leaves it null with a reason,
    /// and the bulk copy carries on exactly as before - it just cannot be tracked.
    /// </summary>
    /// <param name="StartScn">min(S0 - 1, oldest open transaction start - 1); null when it could not be read.</param>
    /// <param name="LoggingReady">ARCHIVELOG plus minimal and primary-key supplemental logging were all on.</param>
    /// <param name="Key">The validated primary key; null when there is none usable (see <paramref name="KeyProblem"/>).</param>
    /// <param name="ObjectIdsJson">Object and data-object ids of the table and its partitions.</param>
    /// <param name="Problems">Plain reasons this copy could not be tracked, if any.</param>
    public sealed record SourceStartPoint(
        decimal? StartScn,
        bool LoggingReady,
        IReadOnlyList<OracleKeyColumn>? Key,
        string? KeyProblem,
        string? ObjectIdsJson,
        IReadOnlyList<string> Problems)
    {
        public bool CanTrack => StartScn != null && LoggingReady && Key != null && ObjectIdsJson != null;
    }

    /// <summary>An Oracle transaction that was still open, and so holds the resume point back.</summary>
    public sealed record OpenTransaction(decimal StartScn, string? Username, string? Program, string? Machine, DateTimeOffset? StartedAt);

    /// <summary>
    /// The three reads a change copy is anchored on, taken in this order: S0, the open transactions,
    /// then S1. The order is what makes <see cref="ResumePoint"/> safe - see ChangeWindow.
    /// </summary>
    public sealed record ScnMarks(decimal S0, decimal? OldestOpenStart, decimal S1, IReadOnlyList<OpenTransaction> OpenTransactions)
    {
        public decimal ResumePoint => ChangeWindow.ResumePoint(S0, OldestOpenStart);
    }

    /// <summary>One table to mine: its key, and the SCN it has already caught up to.</summary>
    public sealed record MiningTable(long Id, string Owner, string Table, IReadOnlyList<OracleKeyColumn> Key, string ObjectIdsJson, decimal FromScn);

    /// <summary>
    /// What the history says about one table: every key it touched, in key-column order and as text
    /// (Oracle NUMBER(38) does not fit a .NET decimal), or the reason the history cannot describe it.
    /// </summary>
    public sealed record MinedTable(long Id, IReadOnlyCollection<IReadOnlyList<string?>> Keys, string? StopReason);

    /// <summary>How change tracking can mine this source, if at all.</summary>
    public enum MiningMode
    {
        /// <summary>A database that is not multitenant: log files are picked by hand.</summary>
        LogFiles,

        /// <summary>A 21c+ pluggable database mining its own changes: Oracle finds the logs itself.</summary>
        PluggableDatabase,

        /// <summary>19c pluggable, or connected to the root container: needs a second connection. Not in v1.</summary>
        Unsupported
    }

    public sealed record ReadinessItem(string Name, bool Ok, string Detail, string? FixSql);

    public sealed record TableReadiness(string Owner, string Table, bool Ok, IReadOnlyList<string> Problems, IReadOnlyList<string> FixSql);

    /// <summary>The readiness check: read-only, and every item says what to ask the DBA for.</summary>
    public sealed record ChangeReadiness(
        MiningMode Mode,
        string Layout,
        string? Version,
        bool Ready,
        IReadOnlyList<ReadinessItem> Items,
        IReadOnlyList<TableReadiness> Tables,
        DateTimeOffset? OldestHistory);

    /// <summary>The history needed for a window is no longer on the server. Only a new bulk copy helps.</summary>
    public sealed class ChangeHistoryGoneException : Exception
    {
        public ChangeHistoryGoneException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>Reads current rows by key for one table, at one fixed SCN.</summary>
    public interface IChangeRowReader : IAsyncDisposable
    {
        /// <summary>
        /// The rows for these keys, with the bulk copy's row filter applied and values normalised exactly
        /// as the bulk reader normalises them, in the tracked column order. A key with no row is simply
        /// absent - that is how a delete, or a row that moved out of the filter, shows up.
        /// </summary>
        Task<IReadOnlyList<object[]>> ReadAsync(IReadOnlyList<IReadOnlyList<string?>> keys, CancellationToken cancellationToken);
    }

    /// <summary>The Oracle side of change tracking.</summary>
    public interface IOracleChangeSource
    {
        /// <summary>
        /// Records where a bulk copy of one table starts. Never throws for an Oracle problem - a bulk copy
        /// must not fail because change tracking is not set up.
        /// </summary>
        Task<SourceStartPoint> CaptureStartPointAsync(Connection connection, string password, string owner, string tableName, CancellationToken cancellationToken);

        /// <summary>Reads S0, the open transactions, then S1 - in that order. Throws rather than guess.</summary>
        Task<ScnMarks> ReadScnMarksAsync(Connection connection, string password, CancellationToken cancellationToken);

        /// <summary>
        /// One pass over the history up to <paramref name="toScn"/> for all the tables, returning the keys
        /// each one touched. A table the history cannot describe gets a <see cref="MinedTable.StopReason"/>;
        /// history missing altogether throws <see cref="ChangeHistoryGoneException"/>.
        /// </summary>
        Task<IReadOnlyList<MinedTable>> MineAsync(Connection connection, string password, IReadOnlyList<MiningTable> tables, decimal toScn, CancellationToken cancellationToken);

        /// <summary>
        /// Opens one session for reading a table's current rows as they were at <paramref name="asOfScn"/>.
        /// One per table, not per batch: every session is unpooled, and opening one per batch of keys would
        /// cost a connection each time.
        /// </summary>
        Task<IChangeRowReader> OpenRowReaderAsync(
            Connection connection, string password, string owner, string table,
            IReadOnlyList<TrackedColumn> columns, string? whereClause, IReadOnlyList<OracleKeyColumn> key,
            decimal asOfScn, CancellationToken cancellationToken);

        /// <summary>What is and is not in place on the source, for each table given.</summary>
        Task<ChangeReadiness> CheckReadinessAsync(Connection connection, string password, IReadOnlyList<(string Owner, string Table)> tables, CancellationToken cancellationToken);
    }
}
