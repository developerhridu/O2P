using O2P.Application.ChangeTracking;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    /// <summary>The destination table a change copy writes, frozen from the bulk copy that set it up.</summary>
    public sealed record ChangeTarget(
        Connection Connection,
        string Password,
        string Schema,
        string Table,
        string? NameStyle,
        IReadOnlyList<TrackedColumn> Columns,
        IReadOnlyList<OracleKeyColumn> Key);

    /// <summary>What one batch did to the destination.</summary>
    public sealed record ApplyResult(long Written, long Deleted);

    /// <summary>Makes a destination table match Oracle for a set of keys.</summary>
    public interface IPostgresChangeApplier
    {
        /// <summary>
        /// Opens one session for a table. Normal mode requires a unique index PostgreSQL can use for
        /// ON CONFLICT on exactly the key columns, and says so plainly if there is none. Settle mode is for
        /// the first copy into a table O2P created, which has no key yet: it deletes every touched key and
        /// inserts the current rows, needing no index at all.
        /// </summary>
        Task<IPostgresChangeSession> OpenAsync(ChangeTarget target, bool settle, CancellationToken cancellationToken);

        /// <summary>
        /// The tables in an order that writes parents before children, from the destination's own foreign
        /// keys. A delete that must go children-first can still fail on the first pass; the change copy
        /// retries a failed table once at the end, after its children have been done. Cycles keep the
        /// given order.
        /// </summary>
        Task<IReadOnlyList<string>> OrderParentsFirstAsync(Connection connection, string password, string schema, IReadOnlyList<string> tables, CancellationToken cancellationToken);
    }

    public interface IPostgresChangeSession : IAsyncDisposable
    {
        /// <summary>
        /// One transaction: every key in <paramref name="keys"/> ends up matching <paramref name="rows"/> -
        /// written if it has a row, deleted if it has none. Repeating it changes nothing further.
        /// </summary>
        Task<ApplyResult> ApplyAsync(IReadOnlyList<IReadOnlyList<string?>> keys, IReadOnlyList<object[]> rows, CancellationToken cancellationToken);

        /// <summary>
        /// Settle mode, after the last batch: looks for a key held by more than one row (a row the fuzzy
        /// bulk load copied twice), and if there is none adds the primary key. Returns the problem, or null.
        /// </summary>
        Task<string?> FinishSettlingAsync(CancellationToken cancellationToken);
    }
}
