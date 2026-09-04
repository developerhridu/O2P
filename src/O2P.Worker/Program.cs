using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using O2P.Application;
using O2P.Infrastructure.Metadata;
using O2P.Infrastructure.Oracle;
using O2P.Infrastructure.Postgres;
using O2P.Worker;
using O2P.Worker.Core;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((hostContext, services) =>
{
    // Register DB context, etc. (assuming done inside these extensions)
    services.AddMetadataInfrastructure(hostContext.Configuration);
    services.AddOracleInfrastructure();
    services.AddPostgresInfrastructure();
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
});

var host = builder.Build();
await host.RunAsync();
