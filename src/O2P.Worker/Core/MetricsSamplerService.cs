using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;

namespace O2P.Worker.Core
{
    public class MetricsSamplerService : BackgroundService
    {
        private readonly ILogger<MetricsSamplerService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly GlobalGovernor _governor;
        private readonly ConcurrentDictionary<long, Totals> _previous = new();

        private sealed record Totals(long Rows, long Bytes, DateTimeOffset At);

        public MetricsSamplerService(ILogger<MetricsSamplerService> logger, IServiceProvider serviceProvider, GlobalGovernor governor)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _governor = governor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SampleMetricsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sampling metrics.");
                }

                await Task.Delay(2000, stoppingToken);
            }
        }

        private async Task SampleMetricsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            // Get all active jobs
            var activeJobs = await db.JobRuns
                .Where(j => j.Status == "Running")
                .ToListAsync(cancellationToken);

            foreach (var job in activeJobs)
            {
                // Running batches report their rows and bytes every few seconds, so these sums move while
                // a batch is in flight rather than only when it commits.
                var totals = await db.ChunkLogs
                    .Where(c => c.TableRun.JobRunId == job.Id)
                    .GroupBy(c => 1)
                    .Select(g => new { Rows = g.Sum(c => c.RowsMigrated), Bytes = g.Sum(c => c.BytesMigrated) })
                    .FirstOrDefaultAsync(cancellationToken);
                var current = new Totals(totals?.Rows ?? 0, totals?.Bytes ?? 0, DateTimeOffset.UtcNow);

                var activeChunks = await db.ChunkLogs
                    .CountAsync(c => c.TableRun.JobRunId == job.Id && c.Status == "Running", cancellationToken);

                var previous = _previous.GetOrAdd(job.Id, current);
                _previous[job.Id] = current;
                var seconds = Math.Max(0.5, (current.At - previous.At).TotalSeconds);

                var sample = new MetricSample
                {
                    JobId = job.Id,
                    JobRunId = job.Id,
                    Timestamp = current.At,
                    // A batch that fails gives its rows back, so a sample can go down; show that as 0.
                    RowsPerSecond = Math.Max(0, (current.Rows - previous.Rows) / seconds),
                    MbPerSecond = Math.Max(0, (current.Bytes - previous.Bytes) / seconds / (1024.0 * 1024.0)),
                    ActiveChunkWorkers = activeChunks,
                    OracleSessions = activeChunks // Simple heuristic
                };

                db.MetricSamples.Add(sample);
            }

            if (activeJobs.Any())
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
