using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Infrastructure.Metadata;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    /// <summary>The figures on the Dashboard's top cards. Read from the metadata database only.</summary>
    [ApiController]
    [Route("api/v1/dashboard")]
    public class DashboardController : ControllerBase
    {
        private static readonly string[] Active = { "Queued", "Running", "Paused" };

        private readonly AppDbContext _db;

        public DashboardController(AppDbContext db) => _db = db;

        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var dayAgo = now.AddHours(-24);

            var runsInProgress = await _db.JobRuns.CountAsync(j => Active.Contains(j.Status), cancellationToken);

            // What committed in the last 24 hours. Batches only count once they are Done: a batch that fails
            // gives its rows back, so counting in-flight work would overstate what arrived.
            var copied = await _db.ChunkLogs.AsNoTracking()
                .Where(c => c.Status == "Done" && c.CompletedAt >= dayAgo)
                .GroupBy(c => 1)
                .Select(g => new { Rows = g.Sum(c => c.RowsMigrated), Bytes = g.Sum(c => c.BytesMigrated) })
                .FirstOrDefaultAsync(cancellationToken);

            // Speed now: the latest sample of each running copy, if it is recent (the Worker samples every 2 s).
            var recent = now.AddSeconds(-30);
            var latest = await _db.MetricSamples.AsNoTracking()
                .Where(m => m.Timestamp >= recent && m.JobRun.Status == "Running")
                .GroupBy(m => m.JobRunId)
                .Select(g => g.OrderByDescending(m => m.Timestamp).First())
                .ToListAsync(cancellationToken);

            var lastRun = await _db.JobRuns.AsNoTracking()
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new
                {
                    j.Id,
                    j.Status,
                    j.Kind,
                    application = j.Application.Name,
                    j.CreatedAt,
                    j.StartedAt,
                    j.CompletedAt
                })
                .FirstOrDefaultAsync(cancellationToken);

            return Ok(new
            {
                runsInProgress,
                copiedRows24h = copied?.Rows ?? 0,
                copiedBytes24h = copied?.Bytes ?? 0,
                rowsPerSecond = latest.Sum(m => m.RowsPerSecond),
                mbPerSecond = latest.Sum(m => m.MbPerSecond),
                lastRun
            });
        }
    }
}
