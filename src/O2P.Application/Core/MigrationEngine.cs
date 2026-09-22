using Microsoft.EntityFrameworkCore;
using O2P.Application.Copying;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
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
        private readonly IOracleChangeSource _changeSource;
        private readonly CopyTuningOptions _tuning;

        public MigrationEngine(
            IAppDbContext db,
            IOracleChunkPlanner chunkPlanner,
            IPostgresDdlExecutor ddlExecutor,
            IPostgresConstraintManager constraintManager,
            IPostgresSchemaInspector schemaInspector,
            IOracleChangeSource changeSource,
            CopyTuningOptions tuning)
        {
            _tuning = tuning;
            _db = db;
            _chunkPlanner = chunkPlanner;
            _ddlExecutor = ddlExecutor;
            _constraintManager = constraintManager;
            _schemaInspector = schemaInspector;
            _changeSource = changeSource;
        }

        /// <summary>
        /// Always returns the base table name (no _mgN suffixes). Rebinds any prior
        /// suffix-0 allocation to this table run so re-runs load into the same name.
        /// </summary>
        public async Task<string> AllocateTargetNameAsync(long tableRunId, long targetConnectionId, string targetSchema, string baseName, CancellationToken cancellationToken = default)
        {
            // The allocation reserves the name O2P would create, and O2P creates in lower case. A run
            // that ends up reusing an older upper-case table still holds this row; it only has to be
            // stable and unique per (connection, schema, name), which the lower-cased form is.
            var normalizedBase = PostgresName.For(baseName.Trim());

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

            // Everything below may create or empty the destination table. A change run's table is a live,
            // tracked table and must never come this way - whatever route led here (a retry, a restart).
            if (JobRunKind.IsChanges(tableRun.JobRun.Kind))
            {
                throw new InvalidOperationException(
                    "A change copy never prepares its tables like a bulk copy, because that would empty them. Start a new change copy instead.");
            }

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

            // Two source columns that differ only in case would collide once lower-cased, and COPY
            // would report it mid-load as "column specified more than once" with no hint where it came
            // from. Refuse here, before the table is touched, naming both.
            var columnCollisions = PostgresName.FindCollisions(
                tableRun.ManifestTable.Columns.Where(c => !c.IsExcluded).Select(c => c.ColumnName));
            if (columnCollisions.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Table {tableRun.ManifestTable.TableName} has columns whose names differ only in upper and lower case, " +
                    $"and destination names are lower case: {PostgresName.DescribeCollisions(columnCollisions)}. " +
                    "Leave one of each pair out of the table selection.");
            }

            // Which name to look for, and what to call the columns. O2P now creates tables in lower
            // case, but tables created by earlier runs are still out there under Oracle's upper-case
            // spelling, so both are checked and whichever is found decides the spelling for this run.
            var lowerTableName = PostgresName.For(tableRun.ManifestTable.TableName);
            var targetTableName = lowerTableName;
            var style = PostgresName.LowerStyle;
            PostgresConstraintSnapshot? snapshot = null;
            var constraintsSuspended = false;

            try
            {
                var tableExisted = await _constraintManager.TableExistsAsync(
                    targetConn!, postgresPassword, targetSchema, targetTableName, cancellationToken);

                if (!tableExisted)
                {
                    // Nothing in lower case. Before creating one, look for the name an earlier run
                    // would have used - otherwise that table is left holding a stale full copy while
                    // a second one is built beside it.
                    var sourceCasedName = tableRun.ManifestTable.TableName;
                    if (!string.Equals(sourceCasedName, lowerTableName, StringComparison.Ordinal)
                        && await _constraintManager.TableExistsAsync(
                            targetConn!, postgresPassword, targetSchema, sourceCasedName, cancellationToken))
                    {
                        targetTableName = sourceCasedName;
                        style = PostgresName.SourceStyle;
                        tableExisted = true;
                    }
                }

                tableRun.TargetTablePreExisted = tableExisted;

                // Record what was resolved before anything is touched, so a failure leaves behind the
                // name that was actually acted on. The Worker reads both back for every batch, which
                // is what keeps all the batches of one run writing the same column names.
                tableRun.TargetTableName = targetTableName;
                tableRun.TargetNameStyle = style;

                // This copy is about to replace the destination table's contents, so whatever change
                // tracking knew about it no longer holds. Mark it before anything is emptied, never
                // after, so a failure part-way cannot leave a tracker claiming to be in step.
                await StopTrackingForBulkCopyAsync(tableRun, targetConn!.Id, targetSchema, targetTableName, cancellationToken);

                if (!tableExisted)
                {
                    // Absent: create the equivalent schema and load into it. A table created a moment
                    // ago has no constraints to preserve and no rows to clear, so the snapshot, drop
                    // and truncate below would every one of them be no-ops.
                    var createTableDdl = PostgresDdlGenerator.GenerateTableDdl(tableRun.ManifestTable, targetSchema, targetTableName, style);
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
                        // A table can be lower case outside and upper case inside - hand-built, or
                        // renamed by someone. Take whichever spelling its columns actually use, so
                        // that case is reported as a plain mismatch rather than failing mid-copy.
                        style = ChooseColumnStyle(tableRun.ManifestTable, liveColumns, style);
                        tableRun.TargetNameStyle = style;

                        var problems = TargetSchemaComparer.Compare(tableRun.ManifestTable, liveColumns, style);
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

                // Where change tracking would continue from. Must be read before the first batch reads
                // a row, which is only after planning. Best effort: without the privileges this copy
                // simply cannot be tracked, and the copy itself is unaffected.
                var startPoint = await _changeSource.CaptureStartPointAsync(
                    sourceConn!, oraclePassword, tableRun.ManifestTable.Owner, tableRun.ManifestTable.TableName, cancellationToken);
                tableRun.SourceStartScn = startPoint.StartScn;
                tableRun.LoggingReadyAtStart = startPoint.LoggingReady;
                tableRun.SourceObjectIdsJson = startPoint.ObjectIdsJson;
                tableRun.SourceKeyJson = startPoint.Key == null ? null : System.Text.Json.JsonSerializer.Serialize(startPoint.Key, ChangeTracking.TrackedTableSetup.Json);
                await _db.SaveChangesAsync(cancellationToken);

                // Batches sized by the table, not a fixed 16: see BatchPlan. LOB tables are recognised
                // from their column types - the HasLobs flag depends on LOB-size statistics that are
                // often missing.
                var hasLobs = tableRun.ManifestTable.HasLobs
                    || tableRun.ManifestTable.Columns.Any(c => !c.IsExcluded && IsLobType(c.OracleDataType));
                var batchCount = BatchPlan.CountFor(tableRun.ManifestTable.EstRows, hasLobs, _tuning);

                var chunks = await _chunkPlanner.PlanChunksAsync(
                    sourceConn!,
                    oraclePassword,
                    tableRun.ManifestTable.Owner,
                    tableRun.ManifestTable.TableName,
                    tableRun.ManifestTable.IsPartitioned,
                    tableRun.ManifestTable.IsIot,
                    batchCount,
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

        private static bool IsLobType(string? oracleType)
        {
            var type = (oracleType ?? string.Empty).Trim().Split('(')[0].Trim().ToUpperInvariant();
            return type is "BLOB" or "CLOB" or "NCLOB" or "LONG" or "LONG RAW" or "BFILE";
        }

        /// <summary>
        /// A bulk copy is about to replace this destination table's contents. Marks its tracker as needing
        /// a fresh start (the bulk copy sets it up again when it finishes), and refuses to go on while a
        /// change copy is using it - two runs writing one table at once would leave it matching neither.
        /// </summary>
        private async Task StopTrackingForBulkCopyAsync(TableRun tableRun, long targetConnectionId, string targetSchema, string targetTableName, CancellationToken cancellationToken)
        {
            // Conditional on the claim being free, so a change copy that claims it a moment earlier wins
            // cleanly instead of both carrying on.
            var marked = await _db.TrackedTables
                .Where(t => t.TargetConnectionId == targetConnectionId
                         && t.TargetSchema == targetSchema
                         && t.TargetTableName == targetTableName
                         && t.ActiveJobRunId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TrackedTableStatus.NeedsBulkCopy)
                    .SetProperty(t => t.LastError, $"Bulk copy run #{tableRun.JobRunId} is replacing this table's contents. Tracking starts again when it finishes.")
                    .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);

            if (marked == 0 && await _db.TrackedTables.AnyAsync(t =>
                    t.TargetConnectionId == targetConnectionId && t.TargetSchema == targetSchema && t.TargetTableName == targetTableName,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    $"A change copy is running for {targetSchema}.{targetTableName}. Wait for it to finish, then retry this table.");
            }
        }

        /// <summary>
        /// Picks the spelling an existing table's columns actually use, by counting how many of the
        /// loaded columns each candidate finds. The table name is only a hint: a table can be lower
        /// case outside and upper case inside, or the other way about. Ties keep <paramref name="preferred"/>,
        /// which is the style the table name suggested, so a table with no matching columns at all is
        /// still reported against the expected spelling rather than an arbitrary one.
        /// </summary>
        private static string ChooseColumnStyle(
            ManifestTable manifest,
            IReadOnlyList<PostgresLiveColumn> liveColumns,
            string preferred)
        {
            var live = new HashSet<string>(liveColumns.Select(c => c.ColumnName), StringComparer.Ordinal);
            var loaded = manifest.Columns.Where(c => !c.IsExcluded).ToList();
            if (loaded.Count == 0) return preferred;

            var lowerHits = loaded.Count(c => live.Contains(PostgresName.For(c.ColumnName)));
            var sourceHits = loaded.Count(c => live.Contains(c.ColumnName));

            if (lowerHits > sourceHits) return PostgresName.LowerStyle;
            if (sourceHits > lowerHits) return PostgresName.SourceStyle;
            return preferred;
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
