using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using O2P.Application;
using O2P.Infrastructure.Metadata;
using O2P.Infrastructure.Oracle;
using O2P.Infrastructure.Postgres;
using O2P.Worker;
using O2P.Worker.Core;
using Serilog;
using System.IO;

var builder = Host.CreateDefaultBuilder(args);

builder.UseSerilog((context, config) =>
{
    var logDir = Path.GetFullPath(Path.Combine(context.HostingEnvironment.ContentRootPath, "..", "..", "logs"));
    Directory.CreateDirectory(logDir);

    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .Enrich.WithProperty("Application", "O2P.Worker")
          .WriteTo.Console()
          .WriteTo.File(
              path: Path.Combine(logDir, "o2p-worker-.log"),
              rollingInterval: RollingInterval.Day,
              retainedFileCountLimit: 14,
              shared: true,
              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({Application}) {Message:lj}{NewLine}{Exception}");
});

builder.ConfigureServices((hostContext, services) =>
{
    // Register DB context, etc. (assuming done inside these extensions)
    services.AddMetadataInfrastructure(hostContext.Configuration);
    services.AddOracleInfrastructure();
    services.AddPostgresInfrastructure();
    // Batch size, stall watchdog, retries and LOB fetching - see docs/Ops_Runbook.md "Bulk copy speed".
    // Registered before AddApplication, which only adds defaults when nothing is registered yet.
    var copying = new O2P.Application.Copying.CopyTuningOptions();
    hostContext.Configuration.GetSection("Copying").Bind(copying);
    services.AddSingleton(copying);
    services.AddApplication();

    // Node-wide concurrency cap. Kept deliberately conservative by default: the source Oracle is
    // often a shared/constrained instance, and too many concurrent sessions cause ORA-50000
    // "connection request timed out" during planning and reading. Tune via config
    // (Concurrency:MaxOracleSessions / Concurrency:MaxChunkWorkers) once the source's real session
    // capacity is known. Chunk workers default to the Oracle-session cap since each holds one session.
    var maxOracleSessions = hostContext.Configuration.GetValue<int>("Concurrency:MaxOracleSessions", 8);
    var maxChunkWorkers = hostContext.Configuration.GetValue<int>("Concurrency:MaxChunkWorkers", maxOracleSessions);
    services.AddSingleton(new GlobalGovernor(maxOracleSessions, maxChunkWorkers));

    services.AddHostedService<O2P.Worker.Worker>();
    services.AddHostedService<MetricsSamplerService>();
    services.AddHostedService<WorkerHeartbeatService>();
});

try
{
    var host = builder.Build();
    try
    {
        var oracleAsm = typeof(Oracle.ManagedDataAccess.Client.OracleConnection).Assembly;
        Log.Information("O2P.Worker starting; Oracle driver loaded from {OracleLocation}", oracleAsm.Location);
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Oracle.ManagedDataAccess failed to load. Worker cannot plan/read Oracle sources.");
        throw;
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "O2P.Worker terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
