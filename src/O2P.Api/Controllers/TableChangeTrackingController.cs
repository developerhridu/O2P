using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using O2P.Domain.Enums;
using O2P.Infrastructure.Metadata;
using System.Text.Json;

namespace O2P.Api.Controllers;

/// <summary>Dashboard change copies use the frozen tracker, independently of migration selections.</summary>
[ApiController]
[Route("api/v1/change-tracking")]
public class TableChangeTrackingController(AppDbContext db, ISecretProtector secrets) : ControllerBase
{
    public static object View(TrackedTable t, string? activeStatus) => new
    {
        t.Id, t.SourceConnectionId, t.TargetConnectionId, t.SourceOwner, t.SourceTable,
        t.TargetSchema, t.TargetTableName, t.Status, t.LastSyncedAt, t.LastError,
        t.ActiveJobRunId, activeStatus, lastScn = t.LastScn?.ToString(),
        heldBackBy = t.HeldBackByJson == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(t.HeldBackByJson)
    };

    public sealed record ReadinessRequest(long SourceConnectionId, string SourceOwner, string SourceTable);

    [HttpPost("readiness")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Readiness(ReadinessRequest request,
        [FromServices] IOracleChangeSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceOwner) || string.IsNullOrWhiteSpace(request.SourceTable))
            return BadRequest("Choose a source table.");
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == request.SourceConnectionId, ct);
        if (connection?.Kind != ConnectionKind.Oracle) return BadRequest("Choose an Oracle source database.");
        try
        {
            return Ok(await source.CheckReadinessAsync(connection,
                secrets.Unprotect(connection.SecretCiphertext),
                new[] { (request.SourceOwner, request.SourceTable) }, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest($"Could not check the source: {ex.Message}");
        }
    }

    public sealed record CopyRequest(string ConfirmationPhrase);

    [HttpPost("{id:long}/copy")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Copy(long id, CopyRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var table = await db.TrackedTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (table == null) return NotFound("This table is no longer tracked.");
        var source = await db.Connections.FindAsync(new object[] { table.SourceConnectionId }, ct);
        var target = await db.Connections.FindAsync(new object[] { table.TargetConnectionId }, ct);
        if (source?.Kind != ConnectionKind.Oracle || target?.Kind != ConnectionKind.Postgres)
            return BadRequest("The source or destination database is no longer available.");

        // Dashboard has no test/live slots. Confirm the exact destination for every copy.
        var phrase = $"COPY {target.Name}/{table.TargetSchema}.{table.TargetTableName}";
        if (!string.Equals(request.ConfirmationPhrase, phrase, StringComparison.Ordinal))
            return BadRequest(new { message = $"Confirm the destination by typing: {phrase}" });
        if (table.Status != TrackedTableStatus.Ready && table.Status != TrackedTableStatus.NeedsFirstSync)
            return Conflict(table.LastError ?? "This table needs a new bulk copy.");

        var job = new JobRun
        {
            Kind = JobRunKind.Changes, Status = "Queued", CreatedAt = DateTimeOffset.UtcNow,
            SourceConnectionId = source.Id, TargetConnectionId = target.Id,
            SourceConnectionName = source.Name, TargetConnectionName = target.Name,
            SourceSlot = "dashboard", TargetSlot = "dashboard", TargetSchema = table.TargetSchema,
            TableRuns = new List<TableRun> { new() {
                TrackedTableId = table.Id, SourceOwner = table.SourceOwner, SourceTable = table.SourceTable,
                TargetTableName = table.TargetTableName, TargetNameStyle = table.TargetNameStyle, Status = "Pending"
            } }
        };
        db.JobRuns.Add(job);
        await db.SaveChangesAsync(ct);
        // Reserve at enqueue time. The worker accepts a reservation owned by its own run.
        var reserved = await db.TrackedTables.Where(t => t.Id == id
                && (t.ActiveJobRunId == null || !db.JobRuns.Any(j => j.Id == t.ActiveJobRunId
                    && (j.Status == "Queued" || j.Status == "Running" || j.Status == "Paused")))
                && t.UpdatedAt == table.UpdatedAt
                && (t.Status == TrackedTableStatus.Ready || t.Status == TrackedTableStatus.NeedsFirstSync))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ActiveJobRunId, job.Id), ct);
        if (reserved != 1) return Conflict("This table is busy or its tracking changed. Refresh and try again.");
        db.RunEvents.Add(new RunEvent {
            JobRunId = job.Id, Actor = User.Identity?.Name ?? "system", Event = "changes.launch_queued",
            DetailJson = JsonSerializer.Serialize(new { trackedTableId = id, destination = phrase }), At = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new { id = job.Id });
    }
}
