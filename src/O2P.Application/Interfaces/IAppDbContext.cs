using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IAppDbContext
    {
        DbSet<Connection> Connections { get; }
        DbSet<O2P.Domain.Entities.Application> Applications { get; }
        DbSet<ApplicationConnection> ApplicationConnections { get; }
        DbSet<Manifest> Manifests { get; }
        DbSet<ManifestTable> ManifestTables { get; }
        DbSet<DiscoveryCache> DiscoveryCaches { get; }
        DbSet<JobRun> JobRuns { get; }
        DbSet<TableRun> TableRuns { get; }
        DbSet<ChunkLog> ChunkLogs { get; }
        DbSet<DiscoveryColumnCache> DiscoveryColumnCaches { get; }
        DbSet<ManifestColumn> ManifestColumns { get; }
        DbSet<ManifestIndex> ManifestIndexes { get; }
        DbSet<JobCommand> JobCommands { get; }
        DbSet<ValidationResult> ValidationResults { get; }
        DbSet<MetricSample> MetricSamples { get; }
        DbSet<TargetNameAllocation> TargetNameAllocations { get; }
        DbSet<RowReject> RowRejects { get; }
        DbSet<RunEvent> RunEvents { get; }
        DbSet<RunLog> RunLogs { get; }
        DbSet<TypeMappingRule> TypeMappingRules { get; }
        DbSet<TrackedTable> TrackedTables { get; }

        DatabaseFacade Database { get; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
        EntityEntry Entry(object entity);
    }
}
