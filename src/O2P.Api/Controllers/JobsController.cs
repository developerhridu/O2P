using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using O2P.Infrastructure.Metadata;
using O2P.Domain.Entities;
using O2P.Application.Core;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Api.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
   // [Authorize(Roles = "Admin,Operator,Viewer")]
    public class JobsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly MigrationEngine _engine;
        private readonly ISecretProtector _secretProtector;
        private readonly ILogger<JobsController> _logger;

        public JobsController(AppDbContext db, MigrationEngine engine, ISecretProtector secretProtector, ILogger<JobsController> logger)
        {
            _db = db;
            _engine = engine;
            _secretProtector = secretProtector;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetJobs()
        {
            var jobs = await _db.JobRuns
                .Include(j => j.Application)
                .Include(j => j.Manifest)
                .OrderByDescending(j => j.CreatedAt)
                .ToListAsync();
            return Ok(jobs);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetJob(long id)
        {
            var job = await _db.JobRuns
                .Include(j => j.Application)
                .Include(j => j.Manifest)
                .Include(j => j.TableRuns)
                .ThenInclude(t => t.ManifestTable)
                .Include(j => j.TableRuns)
                .ThenInclude(t => t.Chunks)
                .FirstOrDefaultAsync(j => j.Id == id);

            if (job == null) return NotFound();
            return Ok(job);
        }

        public class CreateJobRequest
        {
            public long ApplicationId { get; set; }
            public long ManifestId { get; set; }
            public string SourceSlot { get; set; } = "oracle_test";
            public string TargetSlot { get; set; } = "pg_test";
            public string TargetSchema { get; set; } = "public";
        }

        [Authorize(Roles = "Admin,Operator")]
        [HttpPost]
        public async Task<IActionResult> CreateJob([FromBody] CreateJobRequest request)
        {
            var app = await _db.Applications
                .Include(a => a.Connections)
                .FirstOrDefaultAsync(a => a.Id == request.ApplicationId);
            var manifest = await _db.Manifests
                .Include(m => m.Tables)
                .FirstOrDefaultAsync(m => m.Id == request.ManifestId);

            if (app == null || manifest == null)
            {
                return BadRequest("That migration or table selection does not exist.");
            }

            // A run copies the tables that are ticked when it starts. With none ticked it would have
            // nothing to do - and would never finish, since a run is only complete once its tables
            // are. Refuse it here, where the reason can still be explained, rather than queue it.
            if (!manifest.Tables.Any(t => t.Included))
            {
                return BadRequest("This table selection has no tables ticked, so there is nothing to copy. Open it, tick at least one table, save it, and start the run again.");
            }

            // Destination names are lower case, so two ticked tables whose names differ only in case
            // would land on the same one. Unchecked that surfaces as a unique-index violation on
            // table_runs with no hint what caused it, so refuse now and name the pair.
            var tableCollisions = PostgresName.FindCollisions(
                manifest.Tables.Where(t => t.Included).Select(t => t.TableName));
            if (tableCollisions.Count > 0)
            {
                return BadRequest(
                    "This table selection has tables whose names differ only in upper and lower case, and destination " +
                    $"table names are lower case: {PostgresName.DescribeCollisions(tableCollisions)}. " +
                    "Untick one of each pair, then start the run again.");
            }

            // Create JobRun
            var job = new JobRun
            {
                ApplicationId = request.ApplicationId,
                ManifestId = request.ManifestId,
                SourceSlot = request.SourceSlot,
                TargetSlot = request.TargetSlot,
                TargetSchema = request.TargetSchema,
                Status = "Draft",
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.JobRuns.Add(job);
            await _db.SaveChangesAsync();

            // Populate TableRuns. The destination name is the source name in lower case, so the table
            // can be queried without quoting. If an earlier run already made this table under Oracle's
            // upper-case spelling, preparing it finds that one and loads into it instead, rather than
            // building a second copy beside it.
            foreach (var table in manifest.Tables.Where(t => t.Included))
            {
                var tableRun = new TableRun
                {
                    JobRunId = job.Id,
                    ManifestTableId = table.Id,
                    Status = "Pending",
                    TargetTableName = PostgresName.For(table.TableName)
                };
                _db.TableRuns.Add(tableRun);
                await _db.SaveChangesAsync();

                var targetBinding = app.Connections.FirstOrDefault(c => c.Slot == request.TargetSlot);
                if (targetBinding == null)
                {
                    return BadRequest("No destination database has been chosen for this migration.");
                }

                tableRun.TargetTableName = await _engine.AllocateTargetNameAsync(tableRun.Id, targetBinding.ConnectionId, request.TargetSchema, table.TableName);
            }

            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetJob), new { id = job.Id }, job);
        }

        public class LaunchJobRequest
        {
            public string? ConfirmationPhrase { get; set; }
        }

        [Authorize(Roles = "Admin,Operator")]
        [HttpPost("{id}/launch")]
        public async Task<IActionResult> LaunchJob(long id, [FromBody] LaunchJobRequest? request, [FromServices] IPreflightValidatorService preflightValidator)
        {
            var job = await _db.JobRuns
                .Include(j => j.Application)
                .ThenInclude(a => a.Connections)
                .Include(j => j.TableRuns)
                .FirstOrDefaultAsync(j => j.Id == id);

            if (job == null) return NotFound("Job not found.");
            if (job.Status != "Draft") return BadRequest("This run has already been started.");

            // Resolve env connections
            var sourceBinding = job.Application.Connections.FirstOrDefault(c => c.Slot == job.SourceSlot);
            var targetBinding = job.Application.Connections.FirstOrDefault(c => c.Slot == job.TargetSlot);

            if (sourceBinding == null || targetBinding == null)
            {
                return BadRequest("The source or destination database has not been chosen for this migration.");
            }

            var sourceConn = await _db.Connections.FindAsync(sourceBinding.ConnectionId);
            var targetConn = await _db.Connections.FindAsync(targetBinding.ConnectionId);

            if (job.TargetSlot.EndsWith("live", StringComparison.OrdinalIgnoreCase))
            {
                var expectedPhrase = $"MIGRATE {job.Application.Name} LIVE";
                if (!string.Equals(request?.ConfirmationPhrase, expectedPhrase, StringComparison.Ordinal))
                {
                    return BadRequest(new { message = $"Copying into a live database needs confirmation. Type: {expectedPhrase}" });
                }
            }

            var sourcePassword = _secretProtector.Unprotect(sourceConn!.SecretCiphertext ?? System.Array.Empty<byte>());
            var targetPassword = _secretProtector.Unprotect(targetConn!.SecretCiphertext ?? System.Array.Empty<byte>());

            // Run preflight checks. The job's target tables decide whether CREATE rights are needed:
            // tables that already exist are reused as-is, so USAGE + INSERT/TRUNCATE is enough.
            var targetTableNames = job.TableRuns.Select(t => t.TargetTableName).ToList();
            var preflightResult = await preflightValidator.RunPreflightChecksAsync(
                sourceConn, sourcePassword, targetConn, targetPassword, job.TargetSchema, targetTableNames, default);
            if (!preflightResult.Passed)
            {
                _logger.LogWarning("Preflight failed for job {JobId} schema {Schema}: {Details}", job.Id, job.TargetSchema, preflightResult.Details);
                return BadRequest(new
                {
                    message = "The readiness check failed, so the run was not started.",
                    errors = preflightResult.Details,
                    warnings = preflightResult.Warnings
                });
            }

            foreach (var warning in preflightResult.Warnings)
            {
                _logger.LogInformation("Preflight warning for job {JobId}: {Warning}", job.Id, warning);
            }

            job.Status = "Queued";
            _db.JobCommands.Add(new JobCommand
            {
                JobRunId = job.Id,
                Scope = "job",
                Command = "launch",
                IssuedBy = User.Identity?.Name ?? "system",
                IssuedAt = DateTimeOffset.UtcNow
            });
            _db.RunEvents.Add(new RunEvent
            {
                JobRunId = job.Id,
                Actor = User.Identity?.Name ?? "system",
                Event = "job.launch_queued",
                DetailJson = "{}",
                At = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync();

            return Ok(job);
        }

        public class JobCommandRequest
        {
            public string Command { get; set; } = null!;
            public string Scope { get; set; } = "job";
            public string? Payload { get; set; }
        }

        [Authorize(Roles = "Admin,Operator")]
        [HttpPost("{id}/commands")]
        public async Task<IActionResult> CommandJob(long id, [FromBody] JobCommandRequest request)
        {
            var jobKind = await _db.JobRuns.Where(j => j.Id == id).Select(j => j.Kind).FirstOrDefaultAsync();
            if (jobKind == null) return NotFound();

            var allowed = new[] { "pause", "resume", "cancel", "retry_failed", "update_throttle" };
            if (!allowed.Contains(request.Command))
            {
                return BadRequest("That action is not recognised.");
            }

            // A change copy can only be cancelled. Retrying it the bulk way would send its tables back
            // through preparation, which empties them; the way to retry is simply another change copy,
            // which picks up from where the last good one left off.
            if (JobRunKind.IsChanges(jobKind) && request.Command != "cancel")
            {
                return BadRequest(request.Command == "retry_failed"
                    ? "A change copy is not retried. Start a new one with Copy changes; it continues from where the last successful copy left off."
                    : "A change copy can only be cancelled.");
            }

            var now = DateTimeOffset.UtcNow;

            // Cancelling a run that has not started is settled here, straight away. Commands are
            // carried out by the Worker, so if none is running the cancel would sit unprocessed
            // forever and the run would stay "Waiting" no matter how often it was clicked.
            //
            // It is a conditional update on Status = 'Queued', so it cannot clobber a run the Worker
            // has just started: if the Worker got there first, no row matches and this falls through
            // to the normal command, which the Worker handles as it always did. A queued run has no
            // batches and has suspended no constraints, so there is nothing else to undo.
            var cancelledHere = false;
            if (request.Command == "cancel")
            {
                var cancelled = await _db.JobRuns
                    .Where(j => j.Id == id && j.Status == "Queued")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, "Cancelled")
                        .SetProperty(j => j.CompletedAt, now));

                if (cancelled > 0)
                {
                    cancelledHere = true;
                    await _db.TrackedTables.Where(t => t.ActiveJobRunId == id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.ActiveJobRunId, (long?)null));
                    await _db.TableRuns
                        .Where(t => t.JobRunId == id && (t.Status == "Pending"))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.Status, "Cancelled")
                            .SetProperty(t => t.CompletedAt, now)
                            .SetProperty(t => t.ErrorMessage, "Cancelled before it started."));

                    _db.RunEvents.Add(new RunEvent
                    {
                        JobRunId = id,
                        Actor = User.Identity?.Name ?? "system",
                        Event = "job.cancelled_before_start",
                        DetailJson = "{}",
                        At = now
                    });
                }
            }

            _db.JobCommands.Add(new JobCommand
            {
                JobRunId = id,
                Scope = request.Scope,
                Command = request.Command,
                Payload = request.Payload,
                IssuedBy = User.Identity?.Name ?? "system",
                IssuedAt = now,
                // Already carried out above, so a Worker that starts later does not repeat it.
                ProcessedAt = cancelledHere ? now : null
            });

            await _db.SaveChangesAsync();
            return Accepted();
        }

        // Removes a run and its history (tables, batches, checks, events, logs, graphs). Only runs that are
        // not in progress can go: a waiting, running or paused run must be cancelled first, otherwise the
        // Worker could be writing to rows that vanish underneath it. Destination tables are never touched.
        [Authorize(Roles = "Admin,Operator")]
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteJob(long id)
        {
            var job = await _db.JobRuns.AsNoTracking().Where(j => j.Id == id).Select(j => new { j.Id, j.Status }).FirstOrDefaultAsync();
            if (job == null) return NotFound();

            string[] deletable = { "Draft", "Completed", "CompletedWithErrors", "Failed", "Cancelled" };
            if (!deletable.Contains(job.Status))
            {
                return Conflict("This run is still waiting, running or paused. Cancel it first, then delete it.");
            }

            // A run that was cancelled while a batch was mid-copy can still have that batch working for a
            // moment. Wait until nothing in it is active so the Worker's final update does not hit a missing row.
            var stillBusy = await _db.ChunkLogs.AnyAsync(c => c.TableRun.JobRunId == id && c.Status == "Running");
            if (stillBusy)
            {
                return Conflict("A batch of this run is still finishing. Try again in a few seconds.");
            }

            // All or nothing; see RunHistory for what has to be removed explicitly.
            await using var tx = await _db.Database.BeginTransactionAsync();
            await RunHistory.DeleteAsync(_db, _db.JobRuns.Where(j => j.Id == id).Select(j => j.Id));
            await tx.CommitAsync();

            return NoContent();
        }

        // Admin "big red button" for the Job Runs page: cancel every non-terminal job at once and
        // ask the Worker(s) to restart. Only metadata status is changed - target tables are untouched.
        [Authorize(Roles = "Admin")]
        [HttpPost("cancel-all-and-restart-worker")]
        public async Task<IActionResult> CancelAllAndRestartWorker()
        {
            var now = DateTimeOffset.UtcNow;
            string[] terminal = { "Completed", "CompletedWithErrors", "Failed", "Cancelled" };
            string[] active = { "Running", "Queued", "Paused" };

            var activeJobIds = await _db.JobRuns
                .Where(j => active.Contains(j.Status))
                .Select(j => j.Id)
                .ToListAsync();

            var cancelledChunks = 0;
            var cancelledTables = 0;

            if (activeJobIds.Count > 0)
            {
                var tableIds = await _db.TableRuns
                    .Where(t => activeJobIds.Contains(t.JobRunId))
                    .Select(t => t.Id)
                    .ToListAsync();

                cancelledChunks = await _db.ChunkLogs
                    .Where(c => tableIds.Contains(c.TableRunId) && (c.Status == "Pending" || c.Status == "Running"))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "Cancelled"));

                cancelledTables = await _db.TableRuns
                    .Where(t => activeJobIds.Contains(t.JobRunId) && !terminal.Contains(t.Status))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Status, "Cancelled")
                        .SetProperty(t => t.CompletedAt, now));
            }

            var cancelledJobs = await _db.JobRuns
                .Where(j => activeJobIds.Contains(j.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "Cancelled")
                    .SetProperty(j => j.CompletedAt, now));

            // Signal a restart to any running Worker (it polls this row and shuts down for its
            // supervisor to relaunch). Upsert the single control row.
            var control = await _db.WorkerControls.FirstOrDefaultAsync(w => w.Id == 1);
            if (control == null)
            {
                _db.WorkerControls.Add(new WorkerControl { Id = 1, RestartRequestedAt = now });
            }
            else
            {
                control.RestartRequestedAt = now;
            }
            await _db.SaveChangesAsync();

            return Ok(new { cancelledJobs, cancelledTables, cancelledChunks, restartRequestedAt = now });
        }

        [HttpGet("{id}/validation")]
        public async Task<IActionResult> GetValidation(long id)
        {
            var results = await _db.ValidationResults
                .Include(v => v.TableRun)
                .ThenInclude(t => t.ManifestTable)
                .Where(v => v.TableRun.JobRunId == id)
                .ToListAsync();

            return Ok(results);
        }

        [HttpGet("{id}/metrics")]
        public async Task<IActionResult> GetMetrics(long id)
        {
            var samples = await _db.MetricSamples
                .Where(m => m.JobRunId == id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            return Ok(samples);
        }

        [HttpPost("{id}/preflight")]
        public async Task<IActionResult> RunPreflight(long id, [FromServices] IPreflightValidatorService preflightValidator)
        {
            var job = await _db.JobRuns
                .Include(j => j.Application)
                .ThenInclude(a => a.Connections)
                .Include(j => j.TableRuns)
                .FirstOrDefaultAsync(j => j.Id == id);

            if (job == null) return NotFound();
            if (JobRunKind.IsChanges(job.Kind)) return BadRequest("Use Check readiness beside the table on Dashboard.");

            var sourceConnId = job.Application.Connections.First(c => c.Slot == job.SourceSlot).ConnectionId;
            var targetConnId = job.Application.Connections.First(c => c.Slot == job.TargetSlot).ConnectionId;

            var sourceConn = await _db.Connections.FindAsync(sourceConnId);
            var targetConn = await _db.Connections.FindAsync(targetConnId);

            var sourcePassword = _secretProtector.Unprotect(sourceConn!.SecretCiphertext ?? System.Array.Empty<byte>());
            var targetPassword = _secretProtector.Unprotect(targetConn!.SecretCiphertext ?? System.Array.Empty<byte>());

            var targetTableNames = job.TableRuns.Select(t => t.TargetTableName).ToList();
            var result = await preflightValidator.RunPreflightChecksAsync(
                sourceConn, sourcePassword, targetConn, targetPassword, job.TargetSchema, targetTableNames, default);

            return Ok(result);
        }
    }
}
