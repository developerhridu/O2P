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
        /// <summary>Resume fence written by PostgresBinaryWriter; one per target schema.</summary>
        private const string ChunkLogTableName = "_o2p_chunk_log";

        private readonly IAppDbContext _db;
        private readonly IOracleChunkPlanner _chunkPlanner;
        private readonly IPostgresDdlExecutor _ddlExecutor;
        private readonly IPostgresConstraintManager _constraintManager;
        private readonly IPostgresSchemaInspector _schemaInspector;

        public MigrationEngine(
            IAppDbContext db,
            IOracleChunkPlanner chunkPlanner,
            IPostgresDdlExecutor ddlExecutor,
            IPostgresConstraintManager constraintManager,
            IPostgresSchemaInspector schemaInspector)
        {
            _db = db;
            _chunkPlanner = chunkPlanner;
            _ddlExecutor = ddlExecutor;
            _constraintManager = constraintManager;
            _schemaInspector = schemaInspector;
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

                throw new InvalidOperationException($"Could not reserve a destination table name for {baseName}.");
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

            if (tableRun == null) throw new Exception("This table is no longer part of the run.");

            var sourceBinding = tableRun.JobRun.Application.Connections.FirstOrDefault(c => c.Slot == tableRun.JobRun.SourceSlot);
            var targetBinding = tableRun.JobRun.Application.Connections.FirstOrDefault(c => c.Slot == tableRun.JobRun.TargetSlot);

            if (sourceBinding == null || targetBinding == null) throw new Exception("The source or destination database has not been chosen for this migration.");

            var sourceConn = await _db.Connections.FindAsync(new object[] { sourceBinding.ConnectionId }, cancellationToken);
            var targetConn = await _db.Connections.FindAsync(new object[] { targetBinding.ConnectionId }, cancellationToken);

            tableRun.Status = "Creating";
            // Drop any snapshot left over from an earlier attempt at this same TableRun. Without
            // this, a retry that stops at the compatibility check below would leave a stale snapshot
            // for the Worker's safety net, which truncates before restoring - wiping the very table
            // we are refusing to touch.
            tableRun.ConstraintSnapshotJson = null;
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
                tableRun.TargetTablePreExisted = tableExisted;

                if (!tableExisted)
                {
                    // Absent: create the equivalent schema and load into it. A table created a moment
                    // ago has no constraints to preserve and no rows to clear, so the snapshot, drop
                    // and truncate below would every one of them be no-ops.
                    var createTableDdl = PostgresDdlGenerator.GenerateTableDdl(tableRun.ManifestTable, targetSchema, targetTableName);
                    await _ddlExecutor.ExecuteDdlAsync(targetConn!, postgresPassword, createTableDdl, cancellationToken);
                }
                else
                {
                    // Present: leave the schema exactly as it is - no CREATE, no ALTER. Prove it can
                    // accept the manifest BEFORE anything destructive, so a mismatch costs no data.
                    var liveColumns = await _schemaInspector.GetTableColumnsAsync(
                        targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);

                    if (liveColumns != null)
                    {
                        var problems = TargetSchemaComparer.Compare(tableRun.ManifestTable, liveColumns);
                        if (TargetSchemaComparer.HasBlockingProblem(problems))
                        {
                            throw new TargetSchemaMismatchException(targetSchema, targetTableName, problems);
                        }
                    }

                    snapshot = await _constraintManager.SnapshotAsync(
                        targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);
                    tableRun.ConstraintSnapshotJson = PostgresConstraintSnapshot.Serialize(snapshot);
                    await _db.SaveChangesAsync(cancellationToken);

                    // Mark suspended before Drop so any mid-drop failure still triggers restore.
                    constraintsSuspended = snapshot.Constraints.Count > 0;
                    await _constraintManager.DropAsync(targetConn!, postgresPassword, snapshot, cancellationToken);

                    await _constraintManager.TruncateAsync(
                        targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);
                }

                await EnsureChunkLogTableAsync(targetConn!, postgresPassword, targetSchema, cancellationToken);

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
                            $"Preparing the table failed, and its constraints could not be put back. Original problem: {ex.Message}. Restore problem: {restoreEx.Message}",
                            ex);
                    }
                }

                throw;
            }
        }

        /// <summary>
        /// Creates the per-schema chunk fence table up front, while nothing else is running for this
        /// table. The writer opens every chunk with its own CREATE TABLE IF NOT EXISTS for this same
        /// table, but that is not race-safe in Postgres: concurrent chunks can both pass the
        /// existence check, and the losers die on pg_type's unique index
        /// ("duplicate key value violates unique constraint \"pg_type_typname_nsp_index\"").
        /// Creating it once here means the writer's copy always finds it already present.
        /// </summary>
        private async Task EnsureChunkLogTableAsync(
            Connection targetConnection,
            string postgresPassword,
            string targetSchema,
            CancellationToken cancellationToken)
        {
            var ddl = $@"
CREATE TABLE IF NOT EXISTS {SqlIdentifier.QuotePostgresQualified(targetSchema, ChunkLogTableName)} (
    job_run_id bigint NOT NULL,
    table_run_id bigint NOT NULL,
    chunk_index integer NOT NULL,
    completed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (job_run_id, table_run_id, chunk_index)
);";

            try
            {
                await _ddlExecutor.ExecuteDdlAsync(targetConnection, postgresPassword, ddl, cancellationToken);
            }
            catch (Exception)
            {
                // Another worker preparing a different job against this schema may have won the same
                // race. If the table is there now, that is exactly the outcome we wanted.
                var exists = await _constraintManager.TableExistsAsync(
                    targetConnection, postgresPassword, targetSchema, ChunkLogTableName, cancellationToken);
                if (!exists) throw;
            }
        }
    }
}
