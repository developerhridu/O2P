using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using O2P.Infrastructure.Metadata;
using O2P.Domain.Entities;
using O2P.Application.Core;
using O2P.Application.Interfaces;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize(Roles = "Admin,Operator,Viewer")]
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
                return BadRequest("Invalid Application or Manifest selection.");
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

            // Populate TableRuns and always use the source table name (load into existing same-named target).
            foreach (var table in manifest.Tables.Where(t => t.Included))
            {
                var tableRun = new TableRun
                {
                    JobRunId = job.Id,
                    ManifestTableId = table.Id,
                    Status = "Pending",
                    TargetTableName = table.TableName
                };
                _db.TableRuns.Add(tableRun);
                await _db.SaveChangesAsync();

                var targetBinding = app.Connections.FirstOrDefault(c => c.Slot == request.TargetSlot);
                if (targetBinding == null)
                {
                    return BadRequest("Target slot connection is not bound.");
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
            if (job.Status != "Draft") return BadRequest("Only Draft jobs can be launched.");

            // Resolve env connections
            var sourceBinding = job.Application.Connections.FirstOrDefault(c => c.Slot == job.SourceSlot);
            var targetBinding = job.Application.Connections.FirstOrDefault(c => c.Slot == job.TargetSlot);

            if (sourceBinding == null || targetBinding == null)
            {
                return BadRequest("Source or Target slot connections are not bound.");
            }

            var sourceConn = await _db.Connections.FindAsync(sourceBinding.ConnectionId);
            var targetConn = await _db.Connections.FindAsync(targetBinding.ConnectionId);

            if (job.TargetSlot.EndsWith("live", StringComparison.OrdinalIgnoreCase))
            {
                var expectedPhrase = $"MIGRATE {job.Application.Name} LIVE";
                if (!string.Equals(request?.ConfirmationPhrase, expectedPhrase, StringComparison.Ordinal))
                {
                    return BadRequest(new { message = $"Live target requires confirmation phrase: {expectedPhrase}" });
                }
            }

            var sourcePassword = _secretProtector.Unprotect(sourceConn!.SecretCiphertext ?? System.Array.Empty<byte>());
            var targetPassword = _secretProtector.Unprotect(targetConn!.SecretCiphertext ?? System.Array.Empty<byte>());

            // Run preflight checks
            var preflightResult = await preflightValidator.RunPreflightChecksAsync(sourceConn, sourcePassword, targetConn, targetPassword, job.TargetSchema, default);
            if (!preflightResult.Passed)
            {
                _logger.LogWarning("Preflight failed for job {JobId} schema {Schema}: {Details}", job.Id, job.TargetSchema, preflightResult.Details);
                return BadRequest(new
                {
                    message = "Preflight check failed. Job launch aborted.",
                    errors = preflightResult.Details
                });
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
            var jobExists = await _db.JobRuns.AnyAsync(j => j.Id == id);
            if (!jobExists) return NotFound();

            var allowed = new[] { "pause", "resume", "cancel", "retry_failed", "update_throttle" };
            if (!allowed.Contains(request.Command))
            {
                return BadRequest("Unsupported command.");
            }

            _db.JobCommands.Add(new JobCommand
            {
                JobRunId = id,
                Scope = request.Scope,
                Command = request.Command,
                Payload = request.Payload,
                IssuedBy = User.Identity?.Name ?? "system",
                IssuedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync();
            return Accepted();
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
                .FirstOrDefaultAsync(j => j.Id == id);
                
            if (job == null) return NotFound();

            var sourceConnId = job.Application.Connections.First(c => c.Slot == job.SourceSlot).ConnectionId;
            var targetConnId = job.Application.Connections.First(c => c.Slot == job.TargetSlot).ConnectionId;

            var sourceConn = await _db.Connections.FindAsync(sourceConnId);
            var targetConn = await _db.Connections.FindAsync(targetConnId);

            var sourcePassword = _secretProtector.Unprotect(sourceConn!.SecretCiphertext ?? System.Array.Empty<byte>());
            var targetPassword = _secretProtector.Unprotect(targetConn!.SecretCiphertext ?? System.Array.Empty<byte>());

            var result = await preflightValidator.RunPreflightChecksAsync(sourceConn, sourcePassword, targetConn, targetPassword, job.TargetSchema, default);

            return Ok(result);
        }
    }
}
