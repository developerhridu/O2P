using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using O2P.Application.Core;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;
using O2P.Worker.Core;

namespace O2P.Worker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly GlobalGovernor _governor;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly TokenBucketRateLimiter _rateLimiter = new TokenBucketRateLimiter(50000, 10000);
        private DateTimeOffset _workerStartedAt;

        public Worker(ILogger<Worker> logger, IServiceProvider serviceProvider, GlobalGovernor governor, IHostApplicationLifetime lifetime)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _governor = governor;
            _lifetime = lifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerStartedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

            // Start command poller
            _ = Task.Run(() => PollCommandsAsync(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RecoverExpiredLeasesAsync(stoppingToken);
                    await PrepareQueuedJobsAsync(stoppingToken);

                    // Block until a slot is available
                    var lease = await _governor.AcquireWorkerSlotAsync(stoppingToken);
                    
                    // Fire-and-forget to process the chunk concurrently
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool found = await ProcessPendingChunksAsync(stoppingToken);
                            if (!found)
                            {
                                // Back off before releasing and spinning again if no chunks
                                await Task.Delay(2000, stoppingToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing chunk task.");
                        }
                        finally
                        {
                            lease.Dispose();
                        }
                    }, stoppingToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // A transient failure in the control loop (e.g. a brief metadata-DB blip in
                    // RecoverExpiredLeasesAsync/PrepareQueuedJobsAsync) must never take the whole
                    // Worker down - log it and keep polling after a short backoff. Previously any
                    // such exception escaped ExecuteAsync and stopped the BackgroundService (host).
                    _logger.LogError(ex, "Worker control loop iteration failed; backing off and retrying.");
                    try { await Task.Delay(2000, stoppingToken); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        private async Task<bool> ProcessPendingChunksAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var oracleReader = scope.ServiceProvider.GetRequiredService<IOracleDataReader>();
            var pgWriter = scope.ServiceProvider.GetRequiredService<IPostgresBinaryWriter>();
            var secretProtector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

            var workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
            var claimSql = @"
WITH claimed AS (
    SELECT c.""Id""
    FROM o2p.chunk_logs c
    JOIN o2p.table_runs t ON t.""Id"" = c.""TableRunId""
    JOIN o2p.job_runs j ON j.""Id"" = t.""JobRunId""
    WHERE c.""Status"" = 'Pending'
      AND t.""Status"" = 'Loading'
      AND j.""Status"" = 'Running'
    ORDER BY c.""Id""
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE o2p.chunk_logs c
SET ""Status"" = 'Running',
    ""WorkerId"" = @p0,
    ""StartedAt"" = now(),
    ""LeaseExpiresAt"" = now() + interval '10 minutes',
    ""AttemptCount"" = c.""AttemptCount"" + 1
FROM claimed
WHERE c.""Id"" = claimed.""Id""
RETURNING c.""Id"";";

            var claimedChunkId = await ClaimChunkIdAsync(db, claimSql, workerId, cancellationToken);

            if (claimedChunkId == null || claimedChunkId.Value == 0) return false;

            var pendingChunk = await db.ChunkLogs
                .Include(c => c.TableRun)
                .ThenInclude(t => t.JobRun)
                .ThenInclude(j => j.Application)
                .ThenInclude(a => a.Connections)
                .Include(c => c.TableRun)
                .ThenInclude(t => t.ManifestTable)
                .ThenInclude(m => m.Columns)
                .FirstOrDefaultAsync(c => c.Id == claimedChunkId.Value, cancellationToken);

            if (pendingChunk == null) return false;

            _logger.LogInformation($"Claimed chunk {pendingChunk.Id} for table {pendingChunk.TableRun.ManifestTable.TableName}.");

            long rowsWritten = 0;
            bool chunkSucceeded = false;

            // Keep the chunk's lease fresh while it is actively processing. A large single chunk (e.g.
            // a big BLOB table with no parallel split) can run longer than the 10-minute lease; without
            // renewal the lease expires, RecoverExpiredLeasesAsync resets the chunk, and another worker
            // re-claims it - so the table never finishes ("thrash"). The heartbeat extends the lease
            // every few minutes until the chunk completes or fails.
            var heartbeatChunkId = pendingChunk.Id;
            using var leaseCts = new CancellationTokenSource();
            var leaseHeartbeat = Task.Run(async () =>
            {
                while (!leaseCts.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(4), leaseCts.Token); }
                    catch (OperationCanceledException) { break; }
                    try
                    {
                        using var hbScope = _serviceProvider.CreateScope();
                        var hbDb = hbScope.ServiceProvider.GetRequiredService<AppDbContext>();
                        await hbDb.ChunkLogs
                            .Where(c => c.Id == heartbeatChunkId && c.Status == "Running")
                            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(10)), CancellationToken.None);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Lease heartbeat failed for chunk {ChunkId}.", heartbeatChunkId); }
                }
            });

            try
            {
                var sourceConn = pendingChunk.TableRun.JobRun.Application.Connections.First(c => c.Slot == pendingChunk.TableRun.JobRun.SourceSlot).ConnectionId;
                var targetConn = pendingChunk.TableRun.JobRun.Application.Connections.First(c => c.Slot == pendingChunk.TableRun.JobRun.TargetSlot).ConnectionId;

                var oracleConnection = await db.Connections.FindAsync(new object[] { sourceConn }, cancellationToken);
                var pgConnection = await db.Connections.FindAsync(new object[] { targetConn }, cancellationToken);

                var oraclePassword = secretProtector.Unprotect(oracleConnection!.SecretCiphertext ?? new byte[0]);
                var pgPassword = secretProtector.Unprotect(pgConnection!.SecretCiphertext ?? new byte[0]);

                var channel = Channel.CreateBounded<object[]>(new BoundedChannelOptions(1000)
                {
                    SingleWriter = true,
                    SingleReader = true
                });

                var readerTask = oracleReader.ReadChunkDataAsync(
                    oracleConnection,
                    oraclePassword,
                    pendingChunk.TableRun.ManifestTable.Owner,
                    pendingChunk.TableRun.ManifestTable.TableName,
                    pendingChunk.TableRun.ManifestTable.Columns.Where(c => !c.IsExcluded).OrderBy(c => c.Id).ToList(),
                    pendingChunk.TableRun.ManifestTable.WhereClause,
                    pendingChunk,
                    channel.Writer,
                    _rateLimiter,
                    cancellationToken
                );

                var writerTask = pgWriter.WriteDataAsync(
                    pgConnection,
                    pgPassword,
                    pendingChunk.TableRun.JobRun.TargetSchema,
                    pendingChunk.TableRun.TargetTableName,
                    pendingChunk.TableRun.ManifestTable.Columns.Where(c => !c.IsExcluded).OrderBy(c => c.Id).ToList(),
                    pendingChunk.TableRun.JobRunId,
                    pendingChunk.TableRunId,
                    pendingChunk.ChunkIndex,
                    channel.Reader,
                    cancellationToken
                );

                await Task.WhenAll(readerTask, writerTask);

                rowsWritten = writerTask.Result;

                pendingChunk.Status = "Done";
                pendingChunk.CompletedAt = DateTimeOffset.UtcNow;
                pendingChunk.LeaseExpiresAt = null;
                pendingChunk.RowsMigrated = rowsWritten;
                chunkSucceeded = true;
                _logger.LogInformation($"Successfully completed chunk {pendingChunk.Id}. Rows: {rowsWritten}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Chunk {pendingChunk.Id} failed.");
                pendingChunk.Status = "Failed";
                pendingChunk.ErrorMessage = ex.Message;
                pendingChunk.LeaseExpiresAt = null;
            }
            finally
            {
                leaseCts.Cancel();
                try { await leaseHeartbeat; } catch { /* heartbeat cancellation is expected */ }
                await db.SaveChangesAsync(cancellationToken);
            }

            // Roll the completed chunk's row count up to its parent table run with an atomic,
            // database-side increment. A tracked read-modify-write ("TableRun.RowsMigrated += n")
            // loses updates when sibling chunks of the same table finish concurrently in separate
            // scopes, silently undercounting migration progress.
            if (chunkSucceeded && rowsWritten > 0)
            {
                await db.TableRuns
                    .Where(t => t.Id == pendingChunk.TableRunId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RowsMigrated, t => t.RowsMigrated + rowsWritten), cancellationToken);
            }

            // Evaluate table-run (and job) completion now that this chunk reached a terminal state.
            _ = Task.Run(() => CheckAndRunValidationAsync(pendingChunk.TableRunId, cancellationToken), cancellationToken);

            return true;
        }

        private static async Task<long?> ClaimChunkIdAsync(AppDbContext db, string claimSql, string workerId, CancellationToken cancellationToken)
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using var cmd = new NpgsqlCommand(claimSql, connection, tx);
                cmd.Parameters.AddWithValue("p0", workerId);
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task CheckAndRunValidationAsync(long tableRunId, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Bail until every chunk has reached a terminal state. The old check treated only
                // "Done" as terminal, which left a table stuck in "Loading" forever whenever a chunk
                // ended in "Failed" (a failed chunk is never "Done").
                var inProgress = await db.ChunkLogs.AnyAsync(
                    c => c.TableRunId == tableRunId && (c.Status == "Pending" || c.Status == "Running"),
                    cancellationToken);
                if (inProgress) return;

                var chunkCount = await db.ChunkLogs.CountAsync(c => c.TableRunId == tableRunId, cancellationToken);
                if (chunkCount == 0) return; // Nothing planned yet.

                var hasFailedChunks = await db.ChunkLogs.AnyAsync(
                    c => c.TableRunId == tableRunId && c.Status == "Failed", cancellationToken);

                var jobRunId = await db.TableRuns.Where(t => t.Id == tableRunId)
                    .Select(t => t.JobRunId).FirstOrDefaultAsync(cancellationToken);
                if (jobRunId == 0) return;

                if (hasFailedChunks)
                {
                    // Atomically claim the Loading -> Failed transition so only one caller acts.
                    await db.TableRuns
                        .Where(t => t.Id == tableRunId && t.Status == "Loading")
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.Status, "Failed")
                            .SetProperty(t => t.ErrorMessage, "One or more chunks failed. Use retry_failed to reprocess.")
                            .SetProperty(t => t.CompletedAt, DateTimeOffset.UtcNow), cancellationToken);

                    await CheckAndCompleteJobAsync(db, jobRunId, cancellationToken);
                    return;
                }

                // All chunks Done: atomically claim Loading -> Validating so validation runs once,
                // even if several final chunks complete concurrently.
                var claimed = await db.TableRuns
                    .Where(t => t.Id == tableRunId && t.Status == "Loading")
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Validating"), cancellationToken);

                if (claimed == 0)
                {
                    // Another caller already advanced this table past Loading; just re-check the job.
                    await CheckAndCompleteJobAsync(db, jobRunId, cancellationToken);
                    return;
                }

                var tableRun = await db.TableRuns
                    .Include(t => t.JobRun).ThenInclude(j => j.Application).ThenInclude(a => a.Connections)
                    .Include(t => t.ManifestTable)
                    .FirstOrDefaultAsync(t => t.Id == tableRunId, cancellationToken);
                if (tableRun == null) return;

                try
                {
                    var validator = scope.ServiceProvider.GetRequiredService<IValidationService>();
                    var sourceConnId = tableRun.JobRun.Application.Connections.First(c => c.Slot == tableRun.JobRun.SourceSlot).ConnectionId;
                    var targetConnId = tableRun.JobRun.Application.Connections.First(c => c.Slot == tableRun.JobRun.TargetSlot).ConnectionId;

                    var sourceConn = await db.Connections.FindAsync(new object[] { sourceConnId }, cancellationToken);
                    var targetConn = await db.Connections.FindAsync(new object[] { targetConnId }, cancellationToken);

                    var secretProtector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
                    var sourcePassword = secretProtector.Unprotect(sourceConn!.SecretCiphertext ?? new byte[0]);
                    var targetPassword = secretProtector.Unprotect(targetConn!.SecretCiphertext ?? new byte[0]);

                    var result = await validator.ValidateTableRunAsync(tableRun, sourceConn, sourcePassword, targetConn, targetPassword, cancellationToken);
                    db.ValidationResults.Add(result);

                    tableRun.Status = result.Passed ? "Completed" : "CompletedWithErrors";
                    tableRun.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Validation failed for table run {TableRunId}.", tableRunId);
                    tableRun.Status = "CompletedWithErrors";
                    tableRun.ErrorMessage = $"Validation error: {ex.Message}";
                    tableRun.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }

                await CheckAndCompleteJobAsync(db, jobRunId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking or running validation.");
            }
        }

        // Marks a job terminal once all of its table runs have finished. A Paused or Cancelled job
        // is left untouched; the Running -> terminal transition is claimed atomically so concurrent
        // table completions can't race to double-write the final status. Without this, a job stayed
        // "Running" forever even after every table finished.
        private static async Task CheckAndCompleteJobAsync(AppDbContext db, long jobRunId, CancellationToken cancellationToken)
        {
            var terminal = new[] { "Completed", "CompletedWithErrors", "Failed" };

            var statuses = await db.TableRuns
                .Where(t => t.JobRunId == jobRunId)
                .Select(t => t.Status)
                .ToListAsync(cancellationToken);

            if (statuses.Count == 0 || statuses.Any(s => !terminal.Contains(s)))
            {
                return; // Some table run is still in flight.
            }

            var finalStatus = statuses.All(s => s == "Completed") ? "Completed" : "CompletedWithErrors";

            await db.JobRuns
                .Where(j => j.Id == jobRunId && j.Status == "Running")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, finalStatus)
                    .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow), cancellationToken);
        }

        private async Task PollCommandsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // Out-of-band restart signal (set by the "Cancel all & restart worker" admin
                    // action). If a restart was requested after this Worker started, shut down so the
                    // supervisor relaunches a fresh instance.
                    var control = await db.WorkerControls.AsNoTracking()
                        .FirstOrDefaultAsync(w => w.Id == 1, cancellationToken);
                    if (control?.RestartRequestedAt != null && control.RestartRequestedAt > _workerStartedAt)
                    {
                        _logger.LogWarning("Worker restart requested at {At}; stopping for supervisor relaunch.", control.RestartRequestedAt);
                        _lifetime.StopApplication();
                        return;
                    }

                    var unhandledCommands = await db.JobCommands
                        .Where(c => c.ProcessedAt == null)
                        .ToListAsync(cancellationToken);

                    foreach (var cmd in unhandledCommands)
                    {
                        _logger.LogInformation($"Processing command: {cmd.Command} for Job {cmd.JobRunId}");
                        cmd.ProcessedAt = DateTimeOffset.UtcNow;
                        
                        var job = await db.JobRuns.FindAsync(new object[] { cmd.JobRunId }, cancellationToken);
                        if (job != null)
                        {
                            if (cmd.Command == "launch" && job.Status == "Queued") job.Status = "Queued";
                            else if (cmd.Command == "pause") job.Status = "Paused";
                            else if (cmd.Command == "resume") job.Status = "Running";
                            else if (cmd.Command == "cancel") job.Status = "Cancelled";
                            else if (cmd.Command == "retry_failed")
                            {
                                await db.ChunkLogs
                                    .Where(c => c.TableRun!.JobRunId == cmd.JobRunId && c.Status == "Failed")
                                    .ExecuteUpdateAsync(s => s
                                        .SetProperty(c => c.Status, "Pending")
                                        .SetProperty(c => c.ErrorMessage, (string?)null)
                                        .SetProperty(c => c.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

                                // Table runs that failed during planning (e.g. a transient Oracle
                                // connection timeout before any chunks were ever created) have no
                                // chunk_logs rows to reset above - PrepareQueuedJobsAsync only picks
                                // up tables still in "Pending", so put them back there too.
                                await db.TableRuns
                                    .Where(t => t.JobRunId == cmd.JobRunId && t.Status == "Failed")
                                    .ExecuteUpdateAsync(s => s
                                        .SetProperty(t => t.Status, "Pending")
                                        .SetProperty(t => t.ErrorMessage, (string?)null)
                                        .SetProperty(t => t.CompletedAt, (DateTimeOffset?)null), cancellationToken);

                                job.Status = "Running";
                            }
                        }
                    }

                    if (unhandledCommands.Any())
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling commands.");
                }
                await Task.Delay(5000, cancellationToken);
            }
        }

        private async Task PrepareQueuedJobsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var engine = scope.ServiceProvider.GetRequiredService<MigrationEngine>();
            var secretProtector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

            var job = await db.JobRuns
                .Include(j => j.Application).ThenInclude(a => a.Connections)
                .Include(j => j.TableRuns)
                .Where(j => j.Status == "Queued")
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (job == null)
            {
                return;
            }

            var sourceBinding = job.Application.Connections.FirstOrDefault(c => c.Slot == job.SourceSlot);
            var targetBinding = job.Application.Connections.FirstOrDefault(c => c.Slot == job.TargetSlot);
            if (sourceBinding == null || targetBinding == null)
            {
                job.Status = "Failed";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var sourceConn = await db.Connections.FindAsync(new object[] { sourceBinding.ConnectionId }, cancellationToken);
            var targetConn = await db.Connections.FindAsync(new object[] { targetBinding.ConnectionId }, cancellationToken);
            if (sourceConn == null || targetConn == null)
            {
                job.Status = "Failed";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var sourcePassword = secretProtector.Unprotect(sourceConn.SecretCiphertext);
            var targetPassword = secretProtector.Unprotect(targetConn.SecretCiphertext);

            job.Status = "Running";
            job.StartedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var tableRunIds = await db.TableRuns
                .Where(t => t.JobRunId == job.Id && t.Status == "Pending")
                .OrderBy(t => t.Id)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            foreach (var tableRunId in tableRunIds)
            {
                try
                {
                    // Planning opens its own Oracle connection to inspect extents/PK/partitions.
                    // Hold a global Oracle-session slot for that work so planning and chunk-reading
                    // together never exceed the node-wide session cap (prevents ORA-50000 storms).
                    using (await _governor.AcquireOracleSessionAsync(cancellationToken))
                    {
                        await engine.StartTableRunAsync(tableRunId, sourcePassword, targetPassword, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    var table = await db.TableRuns.FindAsync(new object[] { tableRunId }, cancellationToken);
                    if (table != null)
                    {
                        table.Status = "Failed";
                        table.ErrorMessage = ex.Message;
                        table.CompletedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    _logger.LogError(ex, "Failed to prepare table run {TableRunId}", tableRunId);
                }
            }
        }

        private async Task RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ChunkLogs
                .Where(c => c.Status == "Running" && c.LeaseExpiresAt < DateTimeOffset.UtcNow)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, "Pending")
                    .SetProperty(c => c.WorkerId, (string?)null)
                    .SetProperty(c => c.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
        }
    }
}
