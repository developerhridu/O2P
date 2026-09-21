using Microsoft.EntityFrameworkCore;
using O2P.Infrastructure.Metadata;
using System.Linq;
using System.Threading.Tasks;

namespace O2P.Api.Services
{
    /// <summary>
    /// Deleting runs, whether one run, the runs of a table selection, or the runs of a whole migration.
    /// Kept in one place because the database only cascades part of a run's history: rejected rows, events
    /// and logs have no foreign key to the run and would be left behind by a plain delete.
    /// </summary>
    public static class RunHistory
    {
        private static readonly string[] InProgress = { "Queued", "Running", "Paused" };

        /// <summary>
        /// Why these runs cannot be deleted right now, or null. A waiting, running or paused run - or a
        /// cancelled one whose last batch is still finishing - is still writing, and deleting its rows under
        /// the copier would fail its final updates.
        /// </summary>
        public static async Task<string?> WhyNotDeletableAsync(AppDbContext db, IQueryable<long> jobIds)
        {
            var active = await db.JobRuns.Where(j => jobIds.Contains(j.Id) && InProgress.Contains(j.Status))
                .Select(j => j.Id).OrderBy(id => id).Take(5).ToListAsync();
            if (active.Count > 0)
            {
                return $"Run{(active.Count > 1 ? "s" : "")} #{string.Join(", #", active)} {(active.Count > 1 ? "are" : "is")} still waiting, running or paused. Cancel {(active.Count > 1 ? "them" : "it")} first, then try again.";
            }

            if (await db.ChunkLogs.AnyAsync(c => jobIds.Contains(c.TableRun!.JobRunId) && c.Status == "Running"))
            {
                return "A batch of a cancelled run is still finishing. Try again in a few seconds.";
            }

            return null;
        }

        /// <summary>
        /// Removes the runs and everything recorded about them. Dependants without a cascading link go
        /// first; tables, batches and checks cascade from the run. Call inside a transaction. Destination
        /// tables are never touched.
        /// </summary>
        public static async Task DeleteAsync(AppDbContext db, IQueryable<long> jobIds)
        {
            await db.RowRejects.Where(r => db.TableRuns.Any(t => t.Id == r.TableRunId && jobIds.Contains(t.JobRunId))).ExecuteDeleteAsync();
            await db.MetricSamples.Where(m => jobIds.Contains(m.JobRunId)).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => e.JobRunId != null && jobIds.Contains(e.JobRunId.Value)).ExecuteDeleteAsync();
            await db.RunLogs.Where(l => l.JobRunId != null && jobIds.Contains(l.JobRunId.Value)).ExecuteDeleteAsync();
            await db.JobCommands.Where(c => jobIds.Contains(c.JobRunId)).ExecuteDeleteAsync();
            await db.JobRuns.Where(j => jobIds.Contains(j.Id)).ExecuteDeleteAsync();
        }
    }
}
