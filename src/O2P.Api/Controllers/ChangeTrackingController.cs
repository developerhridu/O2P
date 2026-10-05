using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    /// <summary>
    /// "Copy changes": which tables of a migration are tracked, whether the source is ready for it, and
    /// starting a change copy.
    /// </summary>
    [ApiController]
    [Route("api/v1/applications/{appId}")]
    public class ChangeTrackingController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ISecretProtector _secretProtector;

        public ChangeTrackingController(AppDbContext db, ISecretProtector secretProtector)
        {
            _db = db;
            _secretProtector = secretProtector;
        }

        /// <summary>The tracked tables between this migration's source and destination databases.</summary>
        [HttpGet("tracked-tables")]
        public async Task<IActionResult> GetTrackedTables(long appId)
        {
            var app = await _db.Applications.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == appId);
            if (app == null) return NotFound();

            var connectionIds = app.Connections.Select(c => c.ConnectionId).ToList();
            var tracked = await _db.TrackedTables.AsNoTracking()
                .Where(t => connectionIds.Contains(t.SourceConnectionId) && connectionIds.Contains(t.TargetConnectionId))
                .OrderBy(t => t.TargetSchema).ThenBy(t => t.TargetTableName)
                .ToListAsync();

            return Ok(tracked.Select(t => new
            {
                t.Id,
                t.SourceOwner,
                t.SourceTable,
                sourceSlots = app.Connections.Where(c => c.ConnectionId == t.SourceConnectionId).Select(c => c.Slot),
                targetSlots = app.Connections.Where(c => c.ConnectionId == t.TargetConnectionId).Select(c => c.Slot),
                t.TargetSchema,
                t.TargetTableName,
                t.Status,
                // SCNs travel as text: a JSON number would lose precision in the browser past 2^53.
                lastScn = t.LastScn?.ToString(),
                t.LastSyncedAt,
                t.LastError,
                t.ActiveJobRunId,
                heldBackBy = t.HeldBackByJson == null ? (JsonElement?)null : JsonDocument.Parse(t.HeldBackByJson).RootElement,
                t.SetUpFromTableRunId,
                t.UpdatedAt
            }));
        }

        public class ReadinessRequest
        {
            public string SourceSlot { get; set; } = "oracle_test";
            public long ManifestId { get; set; }
        }

        /// <summary>
        /// Read-only check of everything change tracking needs on the source, for the ticked tables of a
        /// selection, with the SQL a DBA would run for anything missing. Changes nothing.
        /// </summary>
        [Authorize(Roles = "Admin,Operator")]
        [HttpPost("change-readiness")]
        public async Task<IActionResult> CheckReadiness(long appId, [FromBody] ReadinessRequest request,
            [FromServices] IOracleChangeSource changeSource, CancellationToken cancellationToken)
        {
            var app = await _db.Applications.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == appId, cancellationToken);
            if (app == null) return NotFound();

            var binding = app.Connections.FirstOrDefault(c => c.Slot == request.SourceSlot);
            if (binding == null) return BadRequest("No source database has been chosen for that slot in this migration.");
            var source = await _db.Connections.FirstAsync(c => c.Id == binding.ConnectionId, cancellationToken);

            var tables = await _db.ManifestTables
                .Where(t => t.ManifestId == request.ManifestId && t.Included && t.Manifest.ApplicationId == appId)
                .Select(t => new { t.Owner, t.TableName })
                .ToListAsync(cancellationToken);

            try
            {
                var password = _secretProtector.Unprotect(source.SecretCiphertext ?? Array.Empty<byte>());
                var result = await changeSource.CheckReadinessAsync(source, password,
                    tables.Select(t => (t.Owner, t.TableName)).ToList(), cancellationToken);
                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return BadRequest($"Could not check the source: {ex.Message}");
            }
        }

        public class CopyChangesRequest
        {
            public string SourceSlot { get; set; } = "oracle_test";
            public string TargetSlot { get; set; } = "pg_test";
            public string TargetSchema { get; set; } = "public";
            public string? ConfirmationPhrase { get; set; }
        }

        /// <summary>
        /// Starts a change copy for every ticked table of a selection that has been set up by a bulk copy
        /// into this destination. Tables that are not ready are listed, with the reason, and left out.
        /// </summary>
        [Authorize(Roles = "Admin,Operator")]
        [HttpPost("manifests/{manifestId}/copy-changes")]
        public async Task<IActionResult> CopyChanges(long appId, long manifestId, [FromBody] CopyChangesRequest request)
        {
            var app = await _db.Applications.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == appId);
            if (app == null) return NotFound();

            var manifest = await _db.Manifests.Include(m => m.Tables).FirstOrDefaultAsync(m => m.Id == manifestId && m.ApplicationId == appId);
            if (manifest == null) return NotFound();

            var sourceBinding = app.Connections.FirstOrDefault(c => c.Slot == request.SourceSlot);
            var targetBinding = app.Connections.FirstOrDefault(c => c.Slot == request.TargetSlot);
            if (sourceBinding == null || targetBinding == null)
            {
                return BadRequest("The source or destination database has not been chosen for this migration.");
            }

            // The same guard as a bulk run: nothing is written into a live database without this phrase.
            if (request.TargetSlot.EndsWith("live", StringComparison.OrdinalIgnoreCase))
            {
                var expectedPhrase = $"MIGRATE {app.Name} LIVE";
                if (!string.Equals(request.ConfirmationPhrase, expectedPhrase, StringComparison.Ordinal))
                {
                    return BadRequest(new { message = $"Copying into a live database needs confirmation. Type: {expectedPhrase}" });
                }
            }

            var tracked = await _db.TrackedTables.AsNoTracking()
                .Where(t => t.SourceConnectionId == sourceBinding.ConnectionId
                         && t.TargetConnectionId == targetBinding.ConnectionId
                         && t.TargetSchema == request.TargetSchema)
                .ToListAsync();

            var included = new List<(ManifestTable Table, TrackedTable Tracked)>();
            var skipped = new List<object>();
            foreach (var table in manifest.Tables.Where(t => t.Included).OrderBy(t => t.Id))
            {
                var match = tracked.FirstOrDefault(t => t.SourceOwner == table.Owner && t.SourceTable == table.TableName);
                if (match == null)
                {
                    skipped.Add(new { table = $"{table.Owner}.{table.TableName}", reason = "Not set up for change tracking in this destination yet. A bulk copy sets it up." });
                }
                else if (match.Status == TrackedTableStatus.NeedsBulkCopy)
                {
                    skipped.Add(new { table = $"{table.Owner}.{table.TableName}", reason = match.LastError ?? "Needs a new bulk copy." });
                }
                else if (match.ActiveJobRunId != null)
                {
                    skipped.Add(new { table = $"{table.Owner}.{table.TableName}", reason = $"Another copy (run #{match.ActiveJobRunId}) is writing it right now." });
                }
                else
                {
                    included.Add((table, match));
                }
            }

            if (included.Count == 0)
            {
                return BadRequest(new { message = "None of the ticked tables can have their changes copied yet.", skipped });
            }

            var job = new JobRun
            {
                ApplicationId = appId,
                ManifestId = manifestId,
                Kind = JobRunKind.Changes,
                SourceSlot = request.SourceSlot,
                TargetSlot = request.TargetSlot,
                TargetSchema = request.TargetSchema,
                // Straight to Queued: a change copy has no preflight of its own - the readiness check is
                // separate and read-only - and its own Worker loop picks it up.
                Status = "Queued",
                CreatedAt = DateTimeOffset.UtcNow
            };
            foreach (var (table, match) in included)
            {
                job.TableRuns.Add(new TableRun
                {
                    ManifestTableId = table.Id,
                    Status = "Pending",
                    TargetTableName = match.TargetTableName,
                    TargetNameStyle = match.TargetNameStyle
                });
            }

            _db.JobRuns.Add(job);
            await _db.SaveChangesAsync();

            _db.RunEvents.Add(new RunEvent
            {
                JobRunId = job.Id,
                Actor = User.Identity?.Name ?? "system",
                Event = "changes.launch_queued",
                DetailJson = JsonSerializer.Serialize(new { tables = included.Count, skipped = skipped.Count }),
                At = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync();

            return Ok(new { id = job.Id, tables = included.Count, skipped });
        }
    }
}
