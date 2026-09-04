using Microsoft.EntityFrameworkCore;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Core
{
    public class MigrationEngine
    {
        private readonly IAppDbContext _db;
        private readonly IOracleChunkPlanner _chunkPlanner;
        private readonly IPostgresDdlExecutor _ddlExecutor;

        public MigrationEngine(IAppDbContext db, IOracleChunkPlanner chunkPlanner, IPostgresDdlExecutor ddlExecutor)
        {
            _db = db;
            _chunkPlanner = chunkPlanner;
            _ddlExecutor = ddlExecutor;
        }

        public async Task<string> AllocateTargetNameAsync(long tableRunId, long targetConnectionId, string targetSchema, string baseName, CancellationToken cancellationToken = default)
        {
            var normalizedBase = baseName.Trim();
            for (var suffix = 0; suffix < 10000; suffix++)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
                var allocation = new TargetNameAllocation
                {
                    TargetConnectionId = targetConnectionId,
                    SchemaName = targetSchema,
                    BaseName = normalizedBase,
                    SuffixNumber = suffix,
                    TableRunId = tableRunId,
                    Status = "reserved",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                _db.TargetNameAllocations.Add(allocation);
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return suffix == 0 ? normalizedBase : $"{normalizedBase}_mg{suffix}";
                }
                catch (DbUpdateException)
                {
                    await tx.RollbackAsync(cancellationToken);
                    // A rolled-back insert stays tracked as "Added" in the change tracker. Without
                    // detaching it here, every subsequent retry in this loop re-sends this same
                    // still-conflicting row alongside the new candidate, so every future attempt
                    // fails too - turning what should be a quick suffix bump into a slow crawl
                    // through all 10000 suffixes before finally throwing.
                    _db.Entry(allocation).State = EntityState.Detached;
                }
            }

            throw new InvalidOperationException($"Could not allocate a target name for {baseName}.");
        }
        
        public async Task StartTableRunAsync(long tableRunId, string oraclePassword, string postgresPassword, CancellationToken cancellationToken)
        {
            var tableRun = await _db.TableRuns
                .Include(t => t.ManifestTable)
                .ThenInclude(m => m.Columns)
                .Include(t => t.ManifestTable)
                .ThenInclude(m => m.Indexes)
                .Include(t => t.JobRun)
                .ThenInclude(j => j.Application)
                .ThenInclude(a => a.Connections)
                .FirstOrDefaultAsync(t => t.Id == tableRunId, cancellationToken);

            if (tableRun == null) throw new Exception("TableRun not found.");

            var sourceBinding = tableRun.JobRun.Application.Connections.FirstOrDefault(c => c.Slot == tableRun.JobRun.SourceSlot);
            var targetBinding = tableRun.JobRun.Application.Connections.FirstOrDefault(c => c.Slot == tableRun.JobRun.TargetSlot);
            
            if (sourceBinding == null || targetBinding == null) throw new Exception("Connections not bound.");
            
            var sourceConn = await _db.Connections.FindAsync(new object[] { sourceBinding.ConnectionId }, cancellationToken);
            var targetConn = await _db.Connections.FindAsync(new object[] { targetBinding.ConnectionId }, cancellationToken);

            // Execute DDL Creation for the pre-migration step
            tableRun.Status = "Creating";
            tableRun.StartedAt ??= DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            var createTableDdl = PostgresDdlGenerator.GenerateTableDdl(tableRun.ManifestTable, tableRun.JobRun.TargetSchema, tableRun.TargetTableName);
            await _ddlExecutor.ExecuteDdlAsync(targetConn!, postgresPassword, createTableDdl, cancellationToken);

            var allocation = await _db.TargetNameAllocations.FirstOrDefaultAsync(a => a.TableRunId == tableRun.Id, cancellationToken);
            if (allocation != null)
            {
                allocation.Status = "created";
                allocation.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // Plan Chunks
            tableRun.Status = "Planning";
            await _db.SaveChangesAsync(cancellationToken);

            var chunks = await _chunkPlanner.PlanChunksAsync(
                sourceConn!,
                oraclePassword,
                tableRun.ManifestTable.Owner,
                tableRun.ManifestTable.TableName,
                tableRun.ManifestTable.IsPartitioned,
                tableRun.ManifestTable.IsIot,
                16, // estimated chunks
                cancellationToken
            );

            // Save chunks
            foreach (var chunk in chunks)
            {
                chunk.TableRunId = tableRun.Id;
                chunk.Status = "Pending";
                _db.ChunkLogs.Add(chunk);
            }

            tableRun.Status = "Loading";
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
