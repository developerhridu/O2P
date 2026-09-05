using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using O2P.Infrastructure.Oracle;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.Reader
{
    /// <summary>
    /// Splits a source table into independently-readable chunks so a large table can be migrated by
    /// many parallel readers (FR-5). Every strategy is strictly read-only against the source:
    ///
    ///   • Heap tables      -> ROWID ranges derived from the segment's extents via DBMS_ROWID
    ///                         (the read-only equivalent of DBMS_PARALLEL_EXECUTE's ROWID chunking;
    ///                         no CREATE/DML, no parallel-execute chunk tables, no extra privileges).
    ///   • Partitioned      -> one lane per partition, each partition further sub-split by ROWID,
    ///                         proportional to its size (partition-wise reads).
    ///   • IOTs / fallback  -> numeric primary-key range chunks bounded by NTILE quantiles.
    ///
    /// Each strategy degrades gracefully: if the data-dictionary views it needs are not granted, it
    /// falls back to the next strategy and ultimately to a single whole-table chunk, so planning
    /// never hard-fails (a single chunk still migrates the table correctly, just without parallelism).
    /// </summary>
    public class OracleChunkPlanner : IOracleChunkPlanner
    {
        // Extended ROWID (type 1) covering the full slot range of a block (rows 0..32767).
        private const int RowidTypeExtended = 1;
        private const int MinRowInBlock = 0;
        private const int MaxRowInBlock = 32767;

        private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "NUMBER", "INTEGER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE"
        };

        public async Task<IEnumerable<ChunkLog>> PlanChunksAsync(Connection connection, string password, string owner, string tableName, bool isPartitioned, bool isIot, int estimatedChunks, CancellationToken cancellationToken)
        {
            if (connection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                var mockChunks = new List<ChunkLog>();
                for (int i = 0; i < 8; i++)
                {
                    mockChunks.Add(new ChunkLog
                    {
                        ChunkIndex = i,
                        Strategy = "rowid",
                        StartRowId = $"MOCK_START_{i}",
                        EndRowId = $"MOCK_END_{i}",
                        Status = "Pending"
                    });
                }
                return mockChunks;
            }

            var targetChunks = Math.Max(1, estimatedChunks);
            var upperOwner = owner.ToUpperInvariant();
            var upperTable = tableName.ToUpperInvariant();

            // Match the reader's connection-string exactly so planning and chunk-reading share one
            // ODP.NET connection pool (fewer distinct pools = fewer sessions churned on the source).
            var csb = new OracleConnectionStringBuilder
            {
                DataSource = $"{connection.Host}:{connection.Port}/{connection.ServiceOrDb}",
                UserID = connection.Username,
                Password = password,
                Pooling = true,
                MinPoolSize = 1,
                MaxPoolSize = 100,
                ConnectionTimeout = 60
            };

            using var conn = new OracleConnection(csb.ConnectionString);
            await OracleConnectionRetry.OpenWithRetryAsync(conn, cancellationToken);

            List<ChunkLog>? chunks;

            if (isIot)
            {
                // IOTs store rows in the primary-key index, so there is no usable physical ROWID to
                // range over; a numeric-PK range is the natural (and only) split.
                chunks = await PlanPkRangeChunksAsync(conn, upperOwner, upperTable, targetChunks, cancellationToken);
            }
            else if (isPartitioned)
            {
                chunks = await PlanPartitionedChunksAsync(conn, upperOwner, upperTable, targetChunks, cancellationToken);
            }
            else
            {
                // Heap table: ROWID-extent ranges are the primary strategy; numeric-PK range is the
                // next fallback when the extent views aren't visible (e.g. cross-schema without
                // dictionary grants). If neither works (no DBA_EXTENTS, no single-column numeric PK),
                // fall back to ROWID-hash buckets so the table still splits into parallel chunks
                // instead of one slow serial chunk - critical for large/LOB tables.
                chunks = await PlanHeapRowidChunksAsync(conn, upperOwner, upperTable, targetChunks, cancellationToken)
                         ?? await PlanPkRangeChunksAsync(conn, upperOwner, upperTable, targetChunks, cancellationToken)
                         ?? PlanRowidHashChunks(targetChunks);
            }

            if (chunks == null || chunks.Count == 0)
            {
                chunks = new List<ChunkLog> { SingleChunk() };
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].ChunkIndex = i;
                chunks[i].Status = "Pending";
            }

            return chunks;
        }

        // Fallback for heap tables with neither visible extents nor a single-column numeric PK: split
        // by hashing the physical ROWID into N buckets. Every row hashes to exactly one bucket, so the
        // chunks are disjoint and cover the whole table. Needs no privileges (pure ORA_HASH), at the
        // cost of N table scans - still far better than one serial chunk for a large or LOB table.
        private static List<ChunkLog> PlanRowidHashChunks(int targetChunks)
        {
            var n = Math.Max(1, targetChunks);
            var chunks = new List<ChunkLog>();
            for (int i = 0; i < n; i++)
            {
                chunks.Add(new ChunkLog
                {
                    Strategy = "rowid_hash",
                    StartRowId = i.ToString(CultureInfo.InvariantCulture),  // bucket index
                    EndRowId = n.ToString(CultureInfo.InvariantCulture),    // total bucket count
                    Status = "Pending"
                });
            }
            return chunks;
        }

        private static ChunkLog SingleChunk(string? partitionName = null) => new()
        {
            Strategy = partitionName == null ? "single" : "partition",
            StartRowId = "MIN",
            EndRowId = "MAX",
            PartitionName = partitionName,
            Status = "Pending"
        };

        // ---- Heap ROWID strategy -------------------------------------------------------------

        private async Task<List<ChunkLog>?> PlanHeapRowidChunksAsync(OracleConnection conn, string owner, string tableName, int targetChunks, CancellationToken cancellationToken)
        {
            var extents = await FetchExtentsAsync(conn, owner, tableName, partitionName: null, cancellationToken);
            if (extents == null || extents.Count == 0)
            {
                return null; // Extent views not visible -> let the caller fall back to PK range.
            }

            long totalBlocks = extents.Sum(e => e.Blocks);
            long target = Math.Max(1, (long)Math.Ceiling(totalBlocks / (double)targetChunks));
            return CoalesceExtents(extents, target, "rowid", partitionName: null);
        }

        // ---- Partition-wise strategy ---------------------------------------------------------

        private async Task<List<ChunkLog>?> PlanPartitionedChunksAsync(OracleConnection conn, string owner, string tableName, int targetChunks, CancellationToken cancellationToken)
        {
            var partitions = await FetchPartitionNamesAsync(conn, owner, tableName, cancellationToken);
            if (partitions == null || partitions.Count == 0)
            {
                // Not visible as partitioned (or denied) -> try a plain heap/PK split on the whole table.
                return await PlanHeapRowidChunksAsync(conn, owner, tableName, targetChunks, cancellationToken);
            }

            // Pull every partition's extents up front so chunks can be sized against the whole table:
            // a big partition earns proportionally more sub-chunks than a small one.
            var byPartition = new Dictionary<string, List<ExtentRange>>();
            long totalBlocks = 0;
            foreach (var partition in partitions)
            {
                var extents = await FetchExtentsAsync(conn, owner, tableName, partition, cancellationToken) ?? new List<ExtentRange>();
                byPartition[partition] = extents;
                totalBlocks += extents.Sum(e => e.Blocks);
            }

            long target = totalBlocks > 0
                ? Math.Max(1, (long)Math.Ceiling(totalBlocks / (double)targetChunks))
                : 1;

            var chunks = new List<ChunkLog>();
            foreach (var partition in partitions)
            {
                var extents = byPartition[partition];
                if (extents.Count == 0)
                {
                    // No extent detail for this partition (composite sub-partitioning, stale stats, or
                    // denied) -> read the whole partition as one lane. Still real inter-partition parallelism.
                    chunks.Add(SingleChunk(partition));
                    continue;
                }

                chunks.AddRange(CoalesceExtents(extents, target, "partition_rowid", partition));
            }

            return chunks.Count > 0 ? chunks : null;
        }

        // ---- Numeric primary-key range strategy ---------------------------------------------

        private async Task<List<ChunkLog>?> PlanPkRangeChunksAsync(OracleConnection conn, string owner, string tableName, int targetChunks, CancellationToken cancellationToken)
        {
            var pkColumn = await GetSingleNumericPkColumnAsync(conn, owner, tableName, cancellationToken);
            if (pkColumn == null)
            {
                return null; // No single-column numeric PK -> caller falls back to a single chunk.
            }

            // Quantile boundaries: MAX of the key within each NTILE bucket, giving up to targetChunks
            // balanced, contiguous ranges over the actual key distribution (handles skew far better
            // than a naive (MAX-MIN)/N split).
            var quotedCol = SqlIdentifier.QuoteOracle(pkColumn);
            var sql = $@"
                SELECT MAX(k) FROM (
                    SELECT {quotedCol} AS k, NTILE(:n) OVER (ORDER BY {quotedCol}) AS grp
                    FROM {SqlIdentifier.OracleQualified(owner, tableName)}
                    WHERE {quotedCol} IS NOT NULL
                ) GROUP BY grp ORDER BY grp";

            var boundaries = new List<string>();
            try
            {
                using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("n", targetChunks));
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.IsDBNull(0)) continue;
                    boundaries.Add(reader.GetDecimal(0).ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is OracleException or OverflowException)
            {
                // View not granted, or a NUMBER key wider than .NET decimal -> fall back to a single chunk.
                return null;
            }

            if (boundaries.Count == 0)
            {
                return null; // Empty table.
            }

            // Half-open ranges keyed on the boundary maxima: chunk 0 is (-inf, b0], the middle chunks
            // are (b[i-1], b[i]], and the last is (b[last-1], +inf). Disjoint and total over all keys.
            var chunks = new List<ChunkLog>();
            for (int i = 0; i < boundaries.Count; i++)
            {
                chunks.Add(new ChunkLog
                {
                    Strategy = "pk_range",
                    BoundColumn = pkColumn,
                    StartRowId = i == 0 ? "MIN" : boundaries[i - 1],
                    EndRowId = i == boundaries.Count - 1 ? "MAX" : boundaries[i],
                    Status = "Pending"
                });
            }

            return chunks;
        }

        // ---- Shared helpers ------------------------------------------------------------------

        private readonly record struct ExtentRange(string StartRowId, string EndRowId, long Blocks);

        /// <summary>
        /// Groups ordered extents into contiguous ROWID ranges of roughly <paramref name="targetBlocks"/>
        /// blocks each. Because extents are read in ROWID order (relative_fno, block_id), each emitted
        /// chunk spans a strictly increasing, non-overlapping ROWID range.
        /// </summary>
        private static List<ChunkLog> CoalesceExtents(List<ExtentRange> extents, long targetBlocks, string strategy, string? partitionName)
        {
            var chunks = new List<ChunkLog>();
            string? startRowId = null;
            string endRowId = string.Empty;
            long accumulated = 0;

            foreach (var extent in extents)
            {
                startRowId ??= extent.StartRowId;
                endRowId = extent.EndRowId;
                accumulated += extent.Blocks;

                if (accumulated >= targetBlocks)
                {
                    chunks.Add(new ChunkLog
                    {
                        Strategy = strategy,
                        StartRowId = startRowId,
                        EndRowId = endRowId,
                        PartitionName = partitionName,
                        Status = "Pending"
                    });
                    startRowId = null;
                    accumulated = 0;
                }
            }

            // Flush the trailing remainder (extents that didn't reach a full target).
            if (startRowId != null)
            {
                chunks.Add(new ChunkLog
                {
                    Strategy = strategy,
                    StartRowId = startRowId,
                    EndRowId = endRowId,
                    PartitionName = partitionName,
                    Status = "Pending"
                });
            }

            return chunks;
        }

        /// <summary>
        /// Reads the extents of a table (or one of its partitions) and materialises each as a start/end
        /// ROWID pair via DBMS_ROWID. Prefers ALL_ views (no exception for typical grants); falls back
        /// to DBA_ only when ALL_ returns nothing (cross-schema / elevated readers).
        /// </summary>
        private static async Task<List<ExtentRange>?> FetchExtentsAsync(OracleConnection conn, string owner, string tableName, string? partitionName, CancellationToken cancellationToken)
        {
            // ALL_ first. Only attempt DBA_ when the session has dictionary access (avoids ORA-00942).
            var views = await CanUseDbaCatalogAsync(conn, cancellationToken)
                ? new[] { "all", "dba" }
                : new[] { "all" };
            foreach (var view in views)
            {
                var objectTypeMatch = partitionName == null
                    ? "o.object_type = 'TABLE' AND o.subobject_name IS NULL"
                    : "o.object_type = 'TABLE PARTITION' AND o.subobject_name = :pname";
                var segmentType = partitionName == null ? "TABLE" : "TABLE PARTITION";
                var partitionFilter = partitionName == null ? string.Empty : "AND e.partition_name = :pname";

                var sql = $@"
                    SELECT
                        DBMS_ROWID.ROWID_CREATE({RowidTypeExtended}, o.data_object_id, e.relative_fno, e.block_id, {MinRowInBlock}) AS start_rid,
                        DBMS_ROWID.ROWID_CREATE({RowidTypeExtended}, o.data_object_id, e.relative_fno, e.block_id + e.blocks - 1, {MaxRowInBlock}) AS end_rid,
                        e.blocks AS blks
                    FROM {view}_extents e
                    JOIN {view}_objects o
                      ON o.owner = e.owner
                     AND o.object_name = e.segment_name
                     AND {objectTypeMatch}
                     AND o.data_object_id IS NOT NULL
                    WHERE e.owner = :owner
                      AND e.segment_name = :tbl
                      AND e.segment_type = :segtype
                      {partitionFilter}
                    ORDER BY e.relative_fno, e.block_id";

                try
                {
                    using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                    cmd.Parameters.Add(new OracleParameter("owner", owner));
                    cmd.Parameters.Add(new OracleParameter("tbl", tableName));
                    cmd.Parameters.Add(new OracleParameter("segtype", segmentType));
                    if (partitionName != null)
                    {
                        cmd.Parameters.Add(new OracleParameter("pname", partitionName));
                    }

                    var extents = new List<ExtentRange>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        extents.Add(new ExtentRange(reader.GetString(0), reader.GetString(1), (long)reader.GetDecimal(2)));
                    }

                    if (extents.Count > 0)
                    {
                        return extents;
                    }
                    // Zero rows from ALL_ can mean "not visible here"; try DBA_ next when available.
                }
                catch (OracleException)
                {
                    // View not granted (ORA-00942) or similar -> try the next view.
                }
            }

            return null;
        }

        private static async Task<List<string>?> FetchPartitionNamesAsync(OracleConnection conn, string owner, string tableName, CancellationToken cancellationToken)
        {
            var views = await CanUseDbaCatalogAsync(conn, cancellationToken)
                ? new[] { "all", "dba" }
                : new[] { "all" };
            foreach (var view in views)
            {
                var sql = $@"
                    SELECT partition_name
                    FROM {view}_tab_partitions
                    WHERE table_owner = :owner AND table_name = :tbl
                    ORDER BY partition_position";
                try
                {
                    using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                    cmd.Parameters.Add(new OracleParameter("owner", owner));
                    cmd.Parameters.Add(new OracleParameter("tbl", tableName));

                    var names = new List<string>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (!reader.IsDBNull(0)) names.Add(reader.GetString(0));
                    }

                    if (names.Count > 0)
                    {
                        return names;
                    }
                }
                catch (OracleException)
                {
                    // Try the next view.
                }
            }

            return null;
        }

        /// <summary>
        /// True when the session can read DBA_ catalog views (SELECT ANY DICTIONARY or equivalent).
        /// Checked via SESSION_PRIVS so we never issue a DBA_ query that would ORA-00942.
        /// </summary>
        private static async Task<bool> CanUseDbaCatalogAsync(OracleConnection conn, CancellationToken cancellationToken)
        {
            using var cmd = new OracleCommand(
                "SELECT COUNT(*) FROM session_privs WHERE privilege = 'SELECT ANY DICTIONARY'",
                conn);
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            return count > 0;
        }

        /// <summary>
        /// Returns the column of a single-column numeric primary key, or null when the table has no PK,
        /// a composite PK, or a non-numeric PK (PK ranges only make sense over an orderable numeric key).
        /// Uses ALL_ views (visible to the reading user) rather than DBA_ views.
        /// </summary>
        private static async Task<string?> GetSingleNumericPkColumnAsync(OracleConnection conn, string owner, string tableName, CancellationToken cancellationToken)
        {
            const string sql = @"
                SELECT tc.column_name, tc.data_type
                FROM all_constraints cons
                JOIN all_cons_columns cc
                  ON cc.owner = cons.owner AND cc.constraint_name = cons.constraint_name
                JOIN all_tab_columns tc
                  ON tc.owner = cc.owner AND tc.table_name = cc.table_name AND tc.column_name = cc.column_name
                WHERE cons.owner = :owner AND cons.table_name = :tbl AND cons.constraint_type = 'P'
                ORDER BY cc.position";

            try
            {
                using var cmd = new OracleCommand(sql, conn) { BindByName = true };
                cmd.Parameters.Add(new OracleParameter("owner", owner));
                cmd.Parameters.Add(new OracleParameter("tbl", tableName));

                var columns = new List<(string Name, string Type)>();
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    columns.Add((reader.GetString(0), reader.GetString(1)));
                }

                if (columns.Count != 1) return null; // No PK or composite PK.
                return NumericTypes.Contains(columns[0].Type) ? columns[0].Name : null;
            }
            catch (OracleException)
            {
                return null;
            }
        }
    }
}
