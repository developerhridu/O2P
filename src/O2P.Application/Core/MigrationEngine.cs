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
        private readonly IPostgresConstraintManager _constraintManager;

        public MigrationEngine(
            IAppDbContext db,
            IOracleChunkPlanner chunkPlanner,
            IPostgresDdlExecutor ddlExecutor,
            IPostgresConstraintManager constraintManager)
        {
            _db = db;
            _chunkPlanner = chunkPlanner;
            _ddlExecutor = ddlExecutor;
            _constraintManager = constraintManager;
        }

        /// <summary>
        /// Always returns the base table name (no _mgN suffixes). Rebinds any prior
        /// suffix-0 allocation to this table run so re-runs load into the same name.
        /// </summary>
        public async Task<string> AllocateTargetNameAsync(long tableRunId, long targetConnectionId, string targetSchema, string baseName, CancellationToken cancellationToken = default)
        {
            var normalizedBase = baseName.Trim();

            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var existingForRun = await _db.TargetNameAllocations
                .FirstOrDefaultAsync(a => a.TableRunId == tableRunId, cancellationToken);

            var existingBase = await _db.TargetNameAllocations
                .FirstOrDefaultAsync(
                    a => a.TargetConnectionId == targetConnectionId
                         && a.SchemaName == targetSchema
                         && a.BaseName == normalizedBase
                         && a.SuffixNumber == 0,
                    cancellationToken);

            if (existingBase != null)
            {
                if (existingForRun != null && existingForRun.Id != existingBase.Id)
                {
                    _db.TargetNameAllocations.Remove(existingForRun);
                }

                existingBase.TableRunId = tableRunId;
                existingBase.Status = "reserved";
                existingBase.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return normalizedBase;
            }

            if (existingForRun != null)
            {
                existingForRun.TargetConnectionId = targetConnectionId;
                existingForRun.SchemaName = targetSchema;
                existingForRun.BaseName = normalizedBase;
                existingForRun.SuffixNumber = 0;
                existingForRun.Status = "reserved";
                existingForRun.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return normalizedBase;
            }

            var allocation = new TargetNameAllocation
            {
                TargetConnectionId = targetConnectionId,
                SchemaName = targetSchema,
                BaseName = normalizedBase,
                SuffixNumber = 0,
                TableRunId = tableRunId,
                Status = "reserved",
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.TargetNameAllocations.Add(allocation);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return normalizedBase;
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(cancellationToken);
                _db.Entry(allocation).State = EntityState.Detached;

                // Concurrent insert of suffix 0 — rebind to this table run.
                var raced = await _db.TargetNameAllocations
                    .FirstOrDefaultAsync(
                        a => a.TargetConnectionId == targetConnectionId
                             && a.SchemaName == targetSchema
                             && a.BaseName == normalizedBase
                             && a.SuffixNumber == 0,
                        cancellationToken);
                if (raced != null)
                {
                    raced.TableRunId = tableRunId;
                    raced.Status = "reserved";
                    raced.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                    return normalizedBase;
                }

                throw new InvalidOperationException($"Could not allocate target name for {baseName}.");
            }
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

            tableRun.Status = "Creating";
            tableRun.StartedAt ??= DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            var targetSchema = tableRun.JobRun.TargetSchema;
            var targetTableName = tableRun.TargetTableName;
            PostgresConstraintSnapshot? snapshot = null;
            var constraintsSuspended = false;

            try
            {
                var tableExisted = await _constraintManager.TableExistsAsync(
                    targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);

                var createTableDdl = PostgresDdlGenerator.GenerateTableDdl(tableRun.ManifestTable, targetSchema, targetTableName);
                await _ddlExecutor.ExecuteDdlAsync(targetConn!, postgresPassword, createTableDdl, cancellationToken);

                snapshot = await _constraintManager.SnapshotAsync(
                    targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);
                tableRun.ConstraintSnapshotJson = PostgresConstraintSnapshot.Serialize(snapshot);
                await _db.SaveChangesAsync(cancellationToken);

                // Mark suspended before Drop so any mid-drop failure still triggers restore.
                constraintsSuspended = snapshot.Constraints.Count > 0;
                await _constraintManager.DropAsync(targetConn!, postgresPassword, snapshot, cancellationToken);

                if (tableExisted)
                {
                    await _constraintManager.TruncateAsync(
                        targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);
                }

                var allocation = await _db.TargetNameAllocations.FirstOrDefaultAsync(a => a.TableRunId == tableRun.Id, cancellationToken);
                if (allocation != null)
                {
                    allocation.Status = "created";
                    allocation.UpdatedAt = DateTimeOffset.UtcNow;
                }

                tableRun.Status = "Planning";
                await _db.SaveChangesAsync(cancellationToken);

                var chunks = await _chunkPlanner.PlanChunksAsync(
                    sourceConn!,
                    oraclePassword,
                    tableRun.ManifestTable.Owner,
                    tableRun.ManifestTable.TableName,
                    tableRun.ManifestTable.IsPartitioned,
                    tableRun.ManifestTable.IsIot,
                    16,
                    cancellationToken
                );

                foreach (var chunk in chunks)
                {
                    chunk.TableRunId = tableRun.Id;
                    chunk.Status = "Pending";
                    _db.ChunkLogs.Add(chunk);
                }

                tableRun.Status = "Loading";
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                if (constraintsSuspended && snapshot != null)
                {
                    try
                    {
                        // Partial rows can block PK/UNIQUE recreate — clear then restore schema.
                        await _constraintManager.TruncateAsync(
                            targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);
                        await _constraintManager.RestoreAsync(targetConn!, postgresPassword, snapshot, cancellationToken);
                    }
                    catch (Exception restoreEx)
                    {
                        throw new InvalidOperationException(
                            $"Table prepare failed and constraint restore also failed. Original: {ex.Message}. Restore: {restoreEx.Message}",
                            ex);
                    }
                }

                throw;
            }
        }
    }
}
