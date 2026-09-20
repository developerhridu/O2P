using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;

namespace O2P.Worker
{
    /// <summary>
    /// Reports "this Worker is alive" to the metadata database every few seconds, so the API and UI
    /// can tell the user whether anything is actually processing runs.
    ///
    /// It is its own hosted service, not part of the main loop, for the same reason job pickup is
    /// separate from chunk copying: a Worker whose copy slots are all busy is still alive, and must
    /// not look dead just because its main loop is occupied.
    /// </summary>
    public class WorkerHeartbeatService : BackgroundService
    {
        // The API treats a Worker as alive if it reported within HeartbeatStaleAfter (45s). Reporting
        // every 10s leaves room for a few missed beats (a slow query, a GC pause, a database blip)
        // before a healthy Worker would be misreported as gone.
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<WorkerHeartbeatService> _logger;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        public WorkerHeartbeatService(IServiceProvider serviceProvider, ILogger<WorkerHeartbeatService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await BeatAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A missed beat is harmless on its own; the next one will catch up. Never let it
                    // take the Worker down.
                    _logger.LogWarning(ex, "Worker heartbeat could not be written; will retry.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task BeatAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;

            var updated = await db.WorkerHeartbeats
                .Where(h => h.InstanceId == _instanceId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.LastSeenAt, now), cancellationToken);

            if (updated == 0)
            {
                // First beat, or the row was pruned. (Re)register this process.
                db.WorkerHeartbeats.Add(new WorkerHeartbeat
                {
                    InstanceId = _instanceId,
                    Host = Environment.MachineName,
                    ProcessId = Environment.ProcessId,
                    StartedAt = _startedAt,
                    LastSeenAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Worker registered (instance {InstanceId}, pid {Pid}).", _instanceId, Environment.ProcessId);
            }

            // Drop rows left behind by Workers that were killed rather than stopped, so the table
            // does not grow forever. Anything silent this long is long past "not alive".
            var cutoff = now.AddDays(-1);
            await db.WorkerHeartbeats
                .Where(h => h.LastSeenAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);

            // A Worker that stops cleanly says so at once, rather than leaving the UI to wait out the
            // staleness window before it notices. (A killed Worker cannot do this; it is caught by
            // the staleness window instead.)
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await db.WorkerHeartbeats
                    .Where(h => h.InstanceId == _instanceId)
                    .ExecuteDeleteAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove the Worker heartbeat on shutdown.");
            }
        }
    }
}
