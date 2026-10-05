using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace O2P.Application.ChangeTracking
{
    /// <summary>One copied column, frozen as the bulk copy wrote it.</summary>
    public sealed record TrackedColumn(string Name, string OracleType, string PostgresType, bool Nullable);

    /// <summary>
    /// Decides whether a finished bulk copy can be tracked, and freezes what change copies will need.
    /// Pure, so the rules can be tested without Oracle.
    /// </summary>
    public static class TrackedTableSetup
    {
        /// <summary>
        /// The one set of options for every JSON column change tracking writes or reads. Written in one
        /// place and read in another, so a mismatch would not fail - System.Text.Json would quietly map
        /// nothing and hand back empty keys.
        /// </summary>
        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Called once every batch of a bulk table is done and its constraints are back. Deliberately not
        /// gated on the row-count check: against a live source that check usually differs, because rows
        /// changed during the load - and change tracking is exactly what brings those back in line.
        /// </summary>
        /// <returns>The values to store, or the reason this copy cannot be tracked.</returns>
        public static (TrackedTable? Table, string? Reason) FromBulkCopy(
            TableRun tableRun, long sourceConnectionId, long targetConnectionId, string targetSchema, DateTimeOffset now)
        {
            if (tableRun.SourceStartScn == null)
            {
                return (null, "Oracle's position in its change history could not be read when this copy started, so there is no point to continue from.");
            }

            if (tableRun.LoggingReadyAtStart != true)
            {
                // Redo written before supplemental logging was on carries no keys, so even switching it on
                // now cannot fill the gap - only a copy started afterwards can be tracked.
                return (null, "The source's change logging was not fully on when this copy started. Switch it on, then run the bulk copy again.");
            }

            if (tableRun.SourceKeyJson == null)
            {
                return (null, "This table has no usable primary key, so a changed or deleted row could not be found in the destination.");
            }

            if (tableRun.SourceObjectIdsJson == null)
            {
                return (null, "The table's Oracle object ids could not be read, so a truncate or move could not be detected.");
            }

            var key = JsonSerializer.Deserialize<List<OracleKeyColumn>>(tableRun.SourceKeyJson, Json) ?? new List<OracleKeyColumn>();
            var columns = tableRun.ManifestTable.Columns
                .Where(c => !c.IsExcluded)
                .OrderBy(c => c.Id)
                .Select(c => new TrackedColumn(c.ColumnName, c.OracleDataType, c.PostgresDataType, c.IsNullable))
                .ToList();

            var copied = new HashSet<string>(columns.Select(c => c.Name), StringComparer.Ordinal);
            var missing = key.Where(k => !copied.Contains(k.Name)).Select(k => k.Name).ToList();
            if (key.Count == 0 || missing.Count > 0)
            {
                return (null, $"The primary key column(s) {string.Join(", ", missing)} were left out of the table selection, so rows could not be matched in the destination.");
            }

            var tracked = new TrackedTable
            {
                SourceOwner = tableRun.ManifestTable.Owner,
                SourceTable = tableRun.ManifestTable.TableName,
                SourceConnectionId = sourceConnectionId,
                ColumnsJson = JsonSerializer.Serialize(columns, Json),
                WhereClause = string.IsNullOrWhiteSpace(tableRun.ManifestTable.WhereClause) ? null : tableRun.ManifestTable.WhereClause,
                KeyColumnsJson = JsonSerializer.Serialize(key, Json),
                ObjectIdsJson = tableRun.SourceObjectIdsJson,
                TargetConnectionId = targetConnectionId,
                TargetSchema = targetSchema,
                TargetTableName = tableRun.TargetTableName,
                TargetNameStyle = tableRun.TargetNameStyle,
                LastScn = tableRun.SourceStartScn,
                // A table this copy created has no key, and adding one straight after a fuzzy load can
                // fail on a row copied twice. The first change copy settles it and adds the key then.
                Status = tableRun.TargetTablePreExisted == true ? TrackedTableStatus.Ready : TrackedTableStatus.NeedsFirstSync,
                ActiveJobRunId = null,
                HeldBackByJson = null,
                SetUpFromTableRunId = tableRun.Id,
                LastSyncedAt = null,
                LastError = null,
                CreatedAt = now,
                UpdatedAt = now,
            };

            return (tracked, null);
        }

        /// <summary>Copies a fresh setup onto an existing row, keeping its identity and creation time.</summary>
        public static void Apply(TrackedTable target, TrackedTable fresh)
        {
            target.SourceConnectionId = fresh.SourceConnectionId;
            target.SourceOwner = fresh.SourceOwner;
            target.SourceTable = fresh.SourceTable;
            target.ColumnsJson = fresh.ColumnsJson;
            target.WhereClause = fresh.WhereClause;
            target.KeyColumnsJson = fresh.KeyColumnsJson;
            target.ObjectIdsJson = fresh.ObjectIdsJson;
            target.TargetNameStyle = fresh.TargetNameStyle;
            target.LastScn = fresh.LastScn;
            target.Status = fresh.Status;
            target.ActiveJobRunId = null;
            target.HeldBackByJson = null;
            target.SetUpFromTableRunId = fresh.SetUpFromTableRunId;
            target.LastSyncedAt = null;
            target.LastError = null;
            target.UpdatedAt = fresh.UpdatedAt;
        }
    }
}
