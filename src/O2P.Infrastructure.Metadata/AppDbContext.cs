using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using O2P.Domain.Enums;
using System;

namespace O2P.Infrastructure.Metadata
{
    public class AppDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IDataProtectionKeyContext, IAppDbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Connection> Connections { get; set; } = null!;
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
        public DbSet<O2P.Domain.Entities.Application> Applications { get; set; } = null!;
        public DbSet<ApplicationConnection> ApplicationConnections { get; set; } = null!;
        public DbSet<Manifest> Manifests { get; set; } = null!;
        public DbSet<ManifestTable> ManifestTables { get; set; } = null!;
        public DbSet<DiscoveryCache> DiscoveryCaches { get; set; } = null!;
        public DbSet<JobRun> JobRuns { get; set; } = null!;
        public DbSet<TableRun> TableRuns { get; set; } = null!;
        public DbSet<ChunkLog> ChunkLogs { get; set; } = null!;
        public DbSet<DiscoveryColumnCache> DiscoveryColumnCaches { get; set; } = null!;
        public DbSet<ManifestColumn> ManifestColumns { get; set; } = null!;
        public DbSet<ManifestIndex> ManifestIndexes { get; set; } = null!;
        public DbSet<JobCommand> JobCommands { get; set; } = null!;
        public DbSet<ValidationResult> ValidationResults { get; set; } = null!;
        public DbSet<MetricSample> MetricSamples { get; set; } = null!;
        public DbSet<TargetNameAllocation> TargetNameAllocations { get; set; } = null!;
        public DbSet<RowReject> RowRejects { get; set; } = null!;
        public DbSet<RunEvent> RunEvents { get; set; } = null!;
        public DbSet<RunLog> RunLogs { get; set; } = null!;
        public DbSet<TypeMappingRule> TypeMappingRules { get; set; } = null!;
        public DbSet<WorkerControl> WorkerControls { get; set; } = null!;
        public DbSet<WorkerHeartbeat> WorkerHeartbeats { get; set; } = null!;
        public DbSet<TrackedTable> TrackedTables { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            
            builder.HasDefaultSchema("o2p");

            // Identity table renaming
            builder.Entity<ApplicationUser>().ToTable("users");
            builder.Entity<ApplicationRole>().ToTable("roles");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("user_roles");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("role_claims");
            builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");

            // Data protection table
            builder.Entity<DataProtectionKey>().ToTable("dataprotection_keys");

            // Connection table mapping
            builder.Entity<Connection>(b =>
            {
                b.ToTable("connections");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                
                b.HasIndex(e => e.Name).IsUnique();
                
                b.Property(e => e.Kind).HasConversion(
                    v => v.ToString().ToLower(),
                    v => (ConnectionKind)Enum.Parse(typeof(ConnectionKind), v, true));

                b.Property(e => e.OptionsJson).HasColumnType("jsonb");
            });

            builder.Entity<O2P.Domain.Entities.Application>(b =>
            {
                b.ToTable("applications");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasIndex(e => e.Name).IsUnique();
                b.Property(e => e.DefaultsJson).HasColumnType("jsonb");
            });

            builder.Entity<ApplicationConnection>(b =>
            {
                b.ToTable("application_connections");
                b.HasKey(e => new { e.ApplicationId, e.Slot });
                b.HasOne(e => e.Application).WithMany(a => a.Connections).HasForeignKey(e => e.ApplicationId);
                b.HasOne(e => e.Connection).WithMany().HasForeignKey(e => e.ConnectionId);
            });

            builder.Entity<Manifest>(b =>
            {
                b.ToTable("manifests");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasIndex(e => new { e.ApplicationId, e.Name, e.Version }).IsUnique();
                b.HasOne(e => e.Application).WithMany(a => a.Manifests).HasForeignKey(e => e.ApplicationId);
            });

            builder.Entity<ManifestTable>(b =>
            {
                b.ToTable("manifest_tables");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.Manifest).WithMany(m => m.Tables).HasForeignKey(e => e.ManifestId);
                b.Property(e => e.ExcludedColumns).HasColumnType("text[]");
            });

            builder.Entity<DiscoveryCache>(b =>
            {
                b.ToTable("discovery_cache");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.Connection).WithMany().HasForeignKey(e => e.ConnectionId);
                b.HasIndex(e => new { e.ConnectionId, e.Owner, e.TableName }).IsUnique();
            });

            builder.Entity<JobRun>(b =>
            {
                b.ToTable("job_runs");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.Application).WithMany().HasForeignKey(e => e.ApplicationId);
                b.HasOne(e => e.Manifest).WithMany().HasForeignKey(e => e.ManifestId);
                b.HasIndex(e => e.Status);
                b.Property(e => e.Kind).HasDefaultValue(JobRunKind.Bulk);
            });

            builder.Entity<TableRun>(b =>
            {
                b.ToTable("table_runs");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.Property(e => e.ConstraintSnapshotJson).HasColumnType("jsonb");
                b.Property(e => e.SourceObjectIdsJson).HasColumnType("jsonb");
                b.Property(e => e.SourceKeyJson).HasColumnType("jsonb");
                b.Property(e => e.SourceStartScn).HasColumnType("numeric");
                b.HasOne(e => e.JobRun).WithMany(j => j.TableRuns).HasForeignKey(e => e.JobRunId);
                b.HasOne(e => e.ManifestTable).WithMany().HasForeignKey(e => e.ManifestTableId);
                b.HasIndex(e => e.Status);
                b.HasIndex(e => new { e.JobRunId, e.TargetTableName }).IsUnique();
            });

            builder.Entity<ChunkLog>(b =>
            {
                b.ToTable("chunk_logs");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.TableRun).WithMany(t => t.Chunks).HasForeignKey(e => e.TableRunId);
                b.HasIndex(e => e.Status);
                b.HasIndex(e => e.WorkerId);
                b.HasIndex(e => e.LeaseExpiresAt);
            });

            builder.Entity<DiscoveryColumnCache>(b =>
            {
                b.ToTable("discovery_column_cache");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.DiscoveryCache).WithMany(d => d.Columns).HasForeignKey(e => e.DiscoveryCacheId);
                b.HasIndex(e => new { e.DiscoveryCacheId, e.ColumnName }).IsUnique();
            });

            builder.Entity<ManifestColumn>(b =>
            {
                b.ToTable("manifest_columns");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.ManifestTable).WithMany(t => t.Columns).HasForeignKey(e => e.ManifestTableId);
                b.HasIndex(e => new { e.ManifestTableId, e.ColumnName }).IsUnique();
            });

            builder.Entity<ManifestIndex>(b =>
            {
                b.ToTable("manifest_indexes");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasOne(e => e.ManifestTable).WithMany(t => t.Indexes).HasForeignKey(e => e.ManifestTableId);
                b.HasIndex(e => new { e.ManifestTableId, e.IndexName }).IsUnique();
            });

            builder.Entity<TargetNameAllocation>(b =>
            {
                b.ToTable("target_name_allocations");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasIndex(e => new { e.TargetConnectionId, e.SchemaName, e.BaseName, e.SuffixNumber }).IsUnique();
                b.HasIndex(e => e.TableRunId).IsUnique();
            });

            builder.Entity<RowReject>(b =>
            {
                b.ToTable("row_rejects");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.Property(e => e.PayloadJson).HasColumnType("jsonb");
                b.HasIndex(e => e.TableRunId);
                b.HasIndex(e => e.ChunkLogId);
            });

            builder.Entity<RunEvent>(b =>
            {
                b.ToTable("run_events");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.Property(e => e.DetailJson).HasColumnType("jsonb");
                b.HasIndex(e => e.JobRunId);
                b.HasIndex(e => e.At);
            });

            builder.Entity<RunLog>(b =>
            {
                b.ToTable("run_logs");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.Property(e => e.PropertiesJson).HasColumnType("jsonb");
                b.HasIndex(e => e.JobRunId);
                b.HasIndex(e => e.Timestamp);
            });

            builder.Entity<WorkerControl>(b =>
            {
                b.ToTable("worker_control");
                b.HasKey(e => e.Id);
                // Single fixed row (Id = 1); we assign the key explicitly, never auto-generate it.
                b.Property(e => e.Id).ValueGeneratedNever();
            });

            builder.Entity<WorkerHeartbeat>(b =>
            {
                b.ToTable("worker_heartbeats");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.HasIndex(e => e.InstanceId).IsUnique();
                b.HasIndex(e => e.LastSeenAt);
            });

            builder.Entity<TrackedTable>(b =>
            {
                b.ToTable("tracked_tables");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.Property(e => e.ColumnsJson).HasColumnType("jsonb");
                b.Property(e => e.KeyColumnsJson).HasColumnType("jsonb");
                b.Property(e => e.ObjectIdsJson).HasColumnType("jsonb");
                b.Property(e => e.HeldBackByJson).HasColumnType("jsonb");
                b.Property(e => e.LastScn).HasColumnType("numeric");
                // What is being kept in sync is the destination table, so that is the identity.
                b.HasIndex(e => new { e.TargetConnectionId, e.TargetSchema, e.TargetTableName }).IsUnique();
                b.HasIndex(e => new { e.SourceConnectionId, e.SourceOwner, e.SourceTable });
                // No relationships, on purpose - see TrackedTable.
            });

            builder.Entity<TypeMappingRule>(b =>
            {
                b.ToTable("type_mapping_rules");
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).UseIdentityAlwaysColumn();
                b.Property(e => e.MatchJson).HasColumnType("jsonb");
                b.Property(e => e.OptionsJson).HasColumnType("jsonb");
                b.HasIndex(e => new { e.Scope, e.ApplicationId, e.ManifestTableId, e.ColumnName });
            });
        }
    }
}
