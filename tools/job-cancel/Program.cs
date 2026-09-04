using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using O2P.Infrastructure.Metadata;

// Cancels the given job runs so a stuck/orphaned job leaves the "Running" state and the worker stops
// considering its tables/chunks. Only metadata status is changed; target tables are NOT dropped.
//
// Env vars:
//   ConnectionStrings__MetadataDb  (or O2P_METADATA_CONN) - metadata DB connection string   [required]
//   O2P_CANCEL_JOBS                - comma-separated job ids, e.g. "10,11"                   [required]
var conn = Environment.GetEnvironmentVariable("ConnectionStrings__MetadataDb")
           ?? Environment.GetEnvironmentVariable("O2P_METADATA_CONN")
           ?? throw new InvalidOperationException("Set ConnectionStrings__MetadataDb or O2P_METADATA_CONN");
var jobIds = (Environment.GetEnvironmentVariable("O2P_CANCEL_JOBS") ?? string.Join(",", args))
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(long.Parse).ToArray();
if (jobIds.Length == 0) { Console.WriteLine("Set O2P_CANCEL_JOBS (e.g. \"10,11\") or pass ids as args"); return 1; }

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:MetadataDb"] = conn })
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IConfiguration>(config);
services.AddMetadataInfrastructure(config);

await using var sp = services.BuildServiceProvider();
using var scope = sp.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

var now = DateTimeOffset.UtcNow;
string[] terminal = { "Completed", "CompletedWithErrors", "Failed", "Cancelled" };

foreach (var jobId in jobIds)
{
    var tableIds = await db.TableRuns.Where(t => t.JobRunId == jobId).Select(t => t.Id).ToListAsync();

    var chunks = await db.ChunkLogs
        .Where(c => tableIds.Contains(c.TableRunId) && (c.Status == "Pending" || c.Status == "Running"))
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "Cancelled"));

    var tables = await db.TableRuns
        .Where(t => t.JobRunId == jobId && !terminal.Contains(t.Status))
        .ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, "Cancelled")
            .SetProperty(t => t.CompletedAt, now));

    var jobs = await db.JobRuns
        .Where(j => j.Id == jobId && j.Status != "Cancelled")
        .ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, "Cancelled")
            .SetProperty(j => j.CompletedAt, now));

    Console.WriteLine($"job#{jobId}: cancelled (job rows={jobs}, tables->Cancelled={tables}, chunks->Cancelled={chunks})");
}

return 0;
