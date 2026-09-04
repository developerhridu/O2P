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
        private readonly ConcurrentDictionary<long, long> _previousRowCounts = new();

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
                var currentTotalRows = await db.ChunkLogs
                    .Where(c => c.TableRun.JobRunId == job.Id)
                    .SumAsync(c => c.RowsMigrated, cancellationToken);

                var activeChunks = await db.ChunkLogs
                    .CountAsync(c => c.TableRun.JobRunId == job.Id && c.Status == "Running", cancellationToken);

                long prevTotalRows = _previousRowCounts.GetOrAdd(job.Id, currentTotalRows);
                long deltaRows = currentTotalRows - prevTotalRows;
                double rowsPerSec = deltaRows / 2.0;

                _previousRowCounts[job.Id] = currentTotalRows;

                var sample = new MetricSample
                {
                    JobId = job.Id,
                    JobRunId = job.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    RowsPerSecond = Math.Max(0, rowsPerSec),
                    MbPerSecond = 0, // MB tracking omitted for now
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
