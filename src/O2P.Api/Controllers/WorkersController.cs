using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Infrastructure.Metadata;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    public class WorkersController : ControllerBase
    {
        // A Worker reports every 10s (see WorkerHeartbeatService). Treat it as gone after this long
        // without a report: enough for several missed beats, short enough that a dead Worker is
        // noticed while someone is still looking at the screen.
        private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(45);

        private readonly AppDbContext _db;

        public WorkersController(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Whether anything is processing runs. Any signed-in user may read it - it exposes no
        /// credentials and answers the question every user has when a run sits at "Waiting".
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - HeartbeatStaleAfter;

            var alive = await _db.WorkerHeartbeats
                .AsNoTracking()
                .Where(h => h.LastSeenAt >= cutoff)
                .OrderBy(h => h.StartedAt)
                .ToListAsync(cancellationToken);

            return Ok(new
            {
                running = alive.Count > 0,
                count = alive.Count,
                // More than one is dangerous, not just untidy: each Worker requeues the other's
                // in-flight batches at startup, so running two can restart active copies.
                multiple = alive.Count > 1,
                workers = alive.Select(h => new
                {
                    host = h.Host,
                    processId = h.ProcessId,
                    startedAt = h.StartedAt,
                    lastSeenAt = h.LastSeenAt
                }),
                staleAfterSeconds = (int)HeartbeatStaleAfter.TotalSeconds,
                serverTime = now
            });
        }
    }
}
