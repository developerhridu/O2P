using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using O2P.Infrastructure.Metadata;

// Read-only diagnostic dump of the most recent job run(s): table + chunk statuses, error messages,
// chunking strategies used, and validation results. No writes. Useful for debugging a failing or
// stuck migration when there is no psql client on the box.
//
// Env vars:
//   ConnectionStrings__MetadataDb  (or O2P_METADATA_CONN) - metadata DB connection string  [required]
var conn = Environment.GetEnvironmentVariable("ConnectionStrings__MetadataDb")
           ?? Environment.GetEnvironmentVariable("O2P_METADATA_CONN")
           ?? throw new InvalidOperationException("Set ConnectionStrings__MetadataDb or O2P_METADATA_CONN");

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

Console.WriteLine("=== Recent job runs ===");
var jobs = await db.JobRuns.AsNoTracking().OrderByDescending(j => j.Id).Take(8).ToListAsync();
foreach (var j in jobs)
    Console.WriteLine($"  job#{j.Id} status={j.Status} created={j.CreatedAt:u} started={j.StartedAt:u} completed={j.CompletedAt:u}");

if (jobs.Count == 0) { Console.WriteLine("(no jobs)"); return 0; }

// Allow inspecting a specific job id via arg; default to the latest.
var jobId = args.Length > 0 && long.TryParse(args[0], out var requested) ? requested : jobs[0].Id;
Console.WriteLine($"\n=== Detail for job#{jobId} ===");

var tables = await db.TableRuns.AsNoTracking()
    .Include(t => t.ManifestTable)
    .Where(t => t.JobRunId == jobId)
    .OrderBy(t => t.Id).ToListAsync();

foreach (var t in tables)
{
    var src = t.ManifestTable is null ? "?" : $"{t.ManifestTable.Owner}.{t.ManifestTable.TableName}";
    Console.WriteLine($"\n  table#{t.Id} {src} -> {t.TargetTableName}");
    Console.WriteLine($"    status={t.Status} rows={t.RowsMigrated} bytes={t.BytesMigrated}");
    if (!string.IsNullOrWhiteSpace(t.ErrorMessage))
        Console.WriteLine($"    ERROR: {t.ErrorMessage}");

    var chunks = await db.ChunkLogs.AsNoTracking().Where(c => c.TableRunId == t.Id).OrderBy(c => c.ChunkIndex).ToListAsync();
    var byStatus = chunks.GroupBy(c => c.Status).Select(g => $"{g.Key}={g.Count()}");
    var strategies = chunks.Select(c => c.Strategy).Distinct();
    Console.WriteLine($"    chunks: total={chunks.Count} [{string.Join(", ", byStatus)}] strategies=[{string.Join(",", strategies)}]");

    foreach (var c in chunks.Where(c => c.Status == "Failed" || !string.IsNullOrWhiteSpace(c.ErrorMessage)).Take(6))
        Console.WriteLine($"      chunk#{c.Id} idx={c.ChunkIndex} strat={c.Strategy} status={c.Status} attempts={c.AttemptCount} part={c.PartitionName} bound={c.BoundColumn} start={c.StartRowId} end={c.EndRowId}\n        ERR: {c.ErrorMessage}");

    var vrs = await db.ValidationResults.AsNoTracking().Where(v => v.TableRunId == t.Id).ToListAsync();
    foreach (var v in vrs)
        Console.WriteLine($"    validation {v.CheckKind}{(v.ColumnName != null ? "/" + v.ColumnName : "")}: src={v.SourceValue} tgt={v.TargetValue} passed={v.Passed}");
}

Console.WriteLine($"\n=== Recent Error/Warning run logs (job#{jobId}) ===");
var logs = await db.RunLogs.AsNoTracking()
    .Where(l => l.JobRunId == jobId && (l.Level == "Error" || l.Level == "Warning"))
    .OrderByDescending(l => l.Id).Take(20).ToListAsync();
foreach (var l in logs.AsEnumerable().Reverse())
    Console.WriteLine($"  [{l.Timestamp:HH:mm:ss}] {l.Level} {l.Source}: {l.Message}");
if (logs.Count == 0) Console.WriteLine("(none)");

return 0;
