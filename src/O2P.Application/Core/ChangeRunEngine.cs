using Microsoft.EntityFrameworkCore;
using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Core
{
    /// <summary>
    /// Runs one change copy ("Copy changes"): finds which rows Oracle touched since each table's last copy
    /// and makes the destination match them. Only ever writes rows; never creates, empties or restores a
    /// table. A table's tracker moves forward only when that table fully succeeded, so any failure - a
    /// crash, a cancel, a busy row - just means the next copy covers the same ground again.
    /// </summary>
    public class ChangeRunEngine
    {
        /// <summary>Keys read and applied per transaction.</summary>
        public const int KeysPerBatch = 1000;

        /// <summary>
        /// A commit can reach Oracle's transaction table a moment before its redo reaches disk, and the log
        /// writer flushes at least every three seconds. Mining waits past that so no committed change sits
        /// in a log LogMiner has not seen yet.
        /// </summary>
        public static readonly TimeSpan RedoFlushWait = TimeSpan.FromSeconds(5);

        private static readonly string[] TerminalTableStatuses = { "Completed", "CompletedWithErrors", "Failed", "Cancelled" };

        private readonly IAppDbContext _db;
        private readonly IOracleChangeSource _source;
        private readonly IPostgresChangeApplier _applier;

        public ChangeRunEngine(IAppDbContext db, IOracleChangeSource source, IPostgresChangeApplier applier)
        {
            _db = db;
            _source = source;
            _applier = applier;
        }

        public async Task RunAsync(long jobRunId, string sourcePassword, string targetPassword, CancellationToken cancellationToken)
        {
            var job = await _db.JobRuns
                .Include(j => j.Application).ThenInclude(a => a.Connections)
                .Include(j => j.TableRuns)
                .FirstAsync(j => j.Id == jobRunId, cancellationToken);

            if (!JobRunKind.IsChanges(job.Kind))
            {
                throw new InvalidOperationException($"Run #{job.Id} is not a change copy.");
            }

            var sourceId = job.Application.Connections.First(c => c.Slot == job.SourceSlot).ConnectionId;
            var targetId = job.Application.Connections.First(c => c.Slot == job.TargetSlot).ConnectionId;
            var source = await _db.Connections.FirstAsync(c => c.Id == sourceId, cancellationToken);
            var target = await _db.Connections.FirstAsync(c => c.Id == targetId, cancellationToken);

            // Tables a copier left mid-way when it died go round again - applying is repeatable.
            foreach (var run in job.TableRuns.Where(t => !TerminalTableStatuses.Contains(t.Status)))
            {
                run.Status = "Pending";
            }
            await _db.SaveChangesAsync(cancellationToken);

            var work = new List<(TableRun Run, TrackedTable Tracked)>();
            try
            {
                foreach (var run in job.TableRuns.Where(t => t.Status == "Pending").OrderBy(t => t.Id))
                {
                    var tracked = await ClaimAsync(job, target.Id, run, cancellationToken);
                    if (tracked != null) work.Add((run, tracked));
                }

                if (work.Count > 0)
                {
                    await CopyAsync(job, source, sourcePassword, target, targetPassword, work, cancellationToken);
                }
            }
            finally
            {
                // Always let go, whatever happened - a claim left behind would block the next copy and
                // any bulk copy of these tables. (The Worker also clears claims held by finished runs.)
                await _db.TrackedTables
                    .Where(t => t.ActiveJobRunId == job.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.ActiveJobRunId, (long?)null), CancellationToken.None);

                await SettleJobAsync(job.Id);
            }
        }

        /// <summary>
        /// Takes the table for this run, atomically: a bulk copy of the same table, or another change copy,
        /// must not write it at the same time.
        /// </summary>
        private async Task<TrackedTable?> ClaimAsync(JobRun job, long targetConnectionId, TableRun run, CancellationToken ct)
        {
            var existing = await _db.TrackedTables.AsNoTracking().FirstOrDefaultAsync(t =>
                t.TargetConnectionId == targetConnectionId && t.TargetSchema == job.TargetSchema && t.TargetTableName == run.TargetTableName, ct);

            if (existing == null)
            {
                await FailAsync(run, "This table is no longer set up for change tracking. Run a bulk copy of it first.", ct);
                return null;
            }

            var claimed = await _db.TrackedTables
                .Where(t => t.Id == existing.Id
                         && (t.ActiveJobRunId == null || t.ActiveJobRunId == job.Id)
                         && (t.Status == TrackedTableStatus.Ready || t.Status == TrackedTableStatus.NeedsFirstSync))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ActiveJobRunId, (long?)job.Id), ct);

            if (claimed == 0)
            {
                var now = await _db.TrackedTables.AsNoTracking().FirstAsync(t => t.Id == existing.Id, ct);
                await FailAsync(run, now.Status == TrackedTableStatus.NeedsBulkCopy
                    ? $"This table needs a new bulk copy before its changes can be copied. {now.LastError}".Trim()
                    : $"Another copy (run #{now.ActiveJobRunId}) is writing this table right now. Try again when it has finished.", ct);
                return null;
            }

            return await _db.TrackedTables.FirstAsync(t => t.Id == existing.Id, ct);
        }

        private async Task CopyAsync(JobRun job, Connection source, string sourcePassword, Connection target, string targetPassword,
            List<(TableRun Run, TrackedTable Tracked)> work, CancellationToken ct)
        {
            ScnMarks marks;
            IReadOnlyList<MinedTable> mined;
            try
            {
                marks = await _source.ReadScnMarksAsync(source, sourcePassword, ct);
                if (!source.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(RedoFlushWait, ct);
                }

                mined = await _source.MineAsync(source, sourcePassword,
                    work.Select(w => new MiningTable(
                        w.Tracked.Id, w.Tracked.SourceOwner, w.Tracked.SourceTable,
                        Deserialize<List<OracleKeyColumn>>(w.Tracked.KeyColumnsJson),
                        w.Tracked.ObjectIdsJson, w.Tracked.LastScn!.Value)).ToList(),
                    marks.S1, ct);
            }
            catch (ChangeHistoryGoneException ex)
            {
                foreach (var (run, tracked) in work)
                {
                    await StopTrackingAsync(tracked, ex.Message, ct);
                    await FailAsync(run, ex.Message, ct);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var (run, _) in work)
                {
                    await FailAsync(run, $"Could not read the source's change history: {ex.Message}", ct);
                }
                return;
            }

            var minedById = mined.ToDictionary(m => m.Id);
            var order = await _applier.OrderParentsFirstAsync(target, targetPassword, job.TargetSchema,
                work.Select(w => w.Tracked.TargetTableName).ToList(), ct);
            var ordered = order.Select(name => work.First(w => w.Tracked.TargetTableName == name)).ToList();

            var retry = new List<(TableRun Run, TrackedTable Tracked)>();
            foreach (var item in ordered)
            {
                var outcome = await CopyTableAsync(job, source, sourcePassword, target, targetPassword, item.Run, item.Tracked, minedById[item.Tracked.Id], marks, ct);
                if (outcome == Outcome.Cancelled) return;
                if (outcome == Outcome.Retryable) retry.Add(item);
            }

            // Once more for anything that failed, now that every other table has been done - which settles
            // a parent whose delete had to wait for its children, and a row that was briefly locked.
            foreach (var item in retry)
            {
                item.Run.Status = "Pending";
                item.Run.ErrorMessage = null;
                if (await CopyTableAsync(job, source, sourcePassword, target, targetPassword, item.Run, item.Tracked, minedById[item.Tracked.Id], marks, ct) == Outcome.Cancelled)
                {
                    return;
                }
            }
        }

        private enum Outcome { Done, Retryable, Cancelled }

        private async Task<Outcome> CopyTableAsync(JobRun job, Connection source, string sourcePassword, Connection target, string targetPassword,
            TableRun run, TrackedTable tracked, MinedTable mined, ScnMarks marks, CancellationToken ct)
        {
            if (mined.StopReason != null)
            {
                await StopTrackingAsync(tracked, mined.StopReason, ct);
                await FailAsync(run, mined.StopReason, ct);
                return Outcome.Done;
            }

            // Counts carry over a retry or a restart: re-applying what is already there writes and deletes
            // nothing (IS DISTINCT FROM, and a deleted key has nothing left to delete), so they stay exact.
            run.Status = "Copying";
            run.StartedAt ??= DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            var columns = Deserialize<List<TrackedColumn>>(tracked.ColumnsJson);
            var key = Deserialize<List<OracleKeyColumn>>(tracked.KeyColumnsJson);
            var settle = tracked.Status == TrackedTableStatus.NeedsFirstSync;

            try
            {
                await using var reader = await _source.OpenRowReaderAsync(source, sourcePassword, tracked.SourceOwner, tracked.SourceTable,
                    columns, tracked.WhereClause, key, marks.S1, ct);
                await using var session = await _applier.OpenAsync(new ChangeTarget(target, targetPassword, job.TargetSchema,
                    tracked.TargetTableName, tracked.TargetNameStyle, columns, key), settle, ct);

                var batches = 0;
                foreach (var batch in mined.Keys.Chunk(KeysPerBatch))
                {
                    if (await IsCancelledAsync(job.Id, ct))
                    {
                        run.Status = "Cancelled";
                        run.CompletedAt = DateTimeOffset.UtcNow;
                        run.ErrorMessage = batches == 0
                            ? "Cancelled before anything was changed in this table."
                            : $"Cancelled after {batches} batch(es). What was already applied stays; the next Copy changes covers the rest.";
                        await _db.SaveChangesAsync(ct);
                        return Outcome.Cancelled;
                    }

                    var rows = await reader.ReadAsync(batch, ct);
                    var applied = await session.ApplyAsync(batch, rows, ct);
                    batches++;

                    run.RowsWritten += applied.Written;
                    run.RowsDeleted += applied.Deleted;
                    run.RowsMigrated = run.RowsWritten + run.RowsDeleted;
                    await _db.SaveChangesAsync(ct);
                }

                if (settle)
                {
                    var problem = await session.FinishSettlingAsync(ct);
                    if (problem != null)
                    {
                        await StopTrackingAsync(tracked, problem, ct);
                        await FailAsync(run, problem, ct);
                        return Outcome.Done;
                    }
                }

                // Only now does the tracker move: everything up to the resume point is in the destination.
                tracked.LastScn = marks.ResumePoint;
                tracked.Status = TrackedTableStatus.Ready;
                tracked.LastSyncedAt = DateTimeOffset.UtcNow;
                tracked.LastError = null;
                tracked.HeldBackByJson = marks.OpenTransactions.Count == 0
                    ? null
                    : JsonSerializer.Serialize(marks.OpenTransactions.Take(5), TrackedTableSetup.Json);
                tracked.UpdatedAt = DateTimeOffset.UtcNow;

                run.Status = "Completed";
                run.ErrorMessage = null;
                run.CompletedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Outcome.Done;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The tracker has not moved, so the next copy re-reads this window. Nothing is lost.
                tracked.LastError = ex.Message;
                tracked.UpdatedAt = DateTimeOffset.UtcNow;
                await FailAsync(run, ex.Message, ct);
                return Outcome.Retryable;
            }
        }

        private async Task<bool> IsCancelledAsync(long jobId, CancellationToken ct) =>
            await _db.JobRuns.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync(ct) == "Cancelled";

        private async Task StopTrackingAsync(TrackedTable tracked, string reason, CancellationToken ct)
        {
            tracked.Status = TrackedTableStatus.NeedsBulkCopy;
            tracked.LastError = reason;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        private async Task FailAsync(TableRun run, string message, CancellationToken ct)
        {
            run.Status = "Failed";
            run.ErrorMessage = message;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// The run's own outcome from its tables. A cancelled run stays cancelled; otherwise every table
        /// copied is Completed, none is Failed, and a mix is CompletedWithErrors.
        /// </summary>
        private async Task SettleJobAsync(long jobId)
        {
            var job = await _db.JobRuns.Include(j => j.TableRuns).FirstAsync(j => j.Id == jobId);
            if (job.Status == "Cancelled")
            {
                foreach (var run in job.TableRuns.Where(t => !TerminalTableStatuses.Contains(t.Status)))
                {
                    run.Status = "Cancelled";
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    run.ErrorMessage ??= "Cancelled before anything was changed in this table.";
                }
            }
            else
            {
                var completed = job.TableRuns.Count(t => t.Status == "Completed");
                job.Status = completed == job.TableRuns.Count ? "Completed" : completed == 0 ? "Failed" : "CompletedWithErrors";
            }

            job.CompletedAt ??= DateTimeOffset.UtcNow;
            job.WorkerId = null;
            job.LeaseExpiresAt = null;
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        private static T Deserialize<T>(string json) where T : new() =>
            JsonSerializer.Deserialize<T>(json, TrackedTableSetup.Json) ?? new T();
    }
}
