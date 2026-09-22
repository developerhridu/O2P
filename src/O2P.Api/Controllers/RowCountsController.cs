using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Application.Interfaces;
using O2P.Application.RowCounts;
using O2P.Domain.Entities;
using O2P.Domain.Enums;
using O2P.Infrastructure.Metadata;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    /// <summary>
    /// The Dashboard's row-count comparison between a source schema and a destination schema.
    ///
    /// Counting is done one table per request, like "Sync counts &amp; sizes": an exact count of a large table
    /// over a VPN can take minutes, and a request per table keeps each one short of any HTTP timeout, lets
    /// the page show progress, and makes Stop cancel just the query that is running. Every result is saved
    /// as it arrives, so the page opens with the last counts and a stopped sync keeps what it did.
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    public class RowCountsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ISecretProtector _secretProtector;

        public RowCountsController(AppDbContext db, ISecretProtector secretProtector)
        {
            _db = db;
            _secretProtector = secretProtector;
        }

        /// <summary>The saved counts of both schemas, paired by table name. Reads nothing from either database.</summary>
        [HttpGet("row-counts")]
        public async Task<IActionResult> GetComparison(
            [FromQuery] long sourceConnectionId,
            [FromQuery] string sourceSchema,
            [FromQuery] long targetConnectionId,
            [FromQuery] string targetSchema,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourceSchema) || string.IsNullOrWhiteSpace(targetSchema))
                return BadRequest("Choose a source schema and a destination schema.");

            var source = await SavedAsync(sourceConnectionId, SourceSchemaName(sourceSchema), cancellationToken);
            var destination = await SavedAsync(targetConnectionId, targetSchema, cancellationToken);

            var pairs = RowCountComparison.Pair(source.Select(ToCount), destination.Select(ToCount));

            return Ok(new
            {
                summary = RowCountComparison.Summarise(pairs),
                source = SideInfo(source),
                destination = SideInfo(destination),
                pairs
            });
        }

        /// <summary>
        /// Schemas that have saved counts for a database. Lets a Viewer, who may not read schemas from the
        /// live database, still pick a comparison someone else synced.
        /// </summary>
        [HttpGet("row-counts/schemas")]
        public async Task<IActionResult> GetSavedSchemas([FromQuery] long connectionId, CancellationToken cancellationToken)
        {
            var schemas = await _db.TableRowCounts.AsNoTracking()
                .Where(c => c.ConnectionId == connectionId)
                .Select(c => c.SchemaName)
                .Distinct()
                .OrderBy(s => s)
                .ToListAsync(cancellationToken);
            return Ok(new { schemas });
        }

        /// <summary>
        /// Reads the schema's current table list from the database and saves it: new tables are added (not
        /// yet counted), tables that no longer exist are removed, and counts of the others are kept.
        /// </summary>
        [HttpPost("connections/{id}/row-counts/tables")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> RefreshTables(
            long id,
            [FromQuery] string schema,
            [FromServices] IOracleDiscoveryService oracle,
            [FromServices] IPostgresSchemaInspector postgres,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(schema)) return BadRequest("A schema name is required.");

            var (conn, password, problem) = await OpenAsync(id, cancellationToken);
            if (problem != null) return problem;

            var schemaName = conn!.Kind == ConnectionKind.Oracle ? SourceSchemaName(schema) : schema;

            IReadOnlyList<string> names;
            try
            {
                names = conn.Kind == ConnectionKind.Oracle
                    ? await oracle.ListTableNamesAsync(conn, password!, schemaName, cancellationToken)
                    : await postgres.ListTableNamesAsync(conn, password!, schemaName, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return BadRequest($"Could not read the tables of {schemaName}: {FirstLine(ex.Message)}");
            }

            var now = DateTimeOffset.UtcNow;
            var saved = await _db.TableRowCounts
                .Where(c => c.ConnectionId == id && c.SchemaName == schemaName)
                .ToListAsync(cancellationToken);
            var byName = saved.ToDictionary(c => c.TableName, StringComparer.Ordinal);
            var present = new HashSet<string>(names, StringComparer.Ordinal);

            foreach (var name in names)
            {
                if (byName.TryGetValue(name, out var existing)) existing.ListedAt = now;
                else _db.TableRowCounts.Add(new TableRowCount { ConnectionId = id, SchemaName = schemaName, TableName = name, ListedAt = now });
            }
            _db.TableRowCounts.RemoveRange(saved.Where(c => !present.Contains(c.TableName)));
            await _db.SaveChangesAsync(CancellationToken.None);

            return Ok(new { schema = schemaName, tables = names, listedAt = now });
        }

        /// <summary>
        /// Exact COUNT(*) of one whole table (ignoring any migration's row filter), saved and returned. A
        /// count that fails is saved as an error on that table and returned normally, so a sync over a
        /// schema carries on to the next table; the previous count is kept alongside the error.
        /// </summary>
        [HttpPost("connections/{id}/row-counts/count")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> CountTable(
            long id,
            [FromQuery] string schema,
            [FromQuery] string table,
            [FromServices] ISourceCountExecutor sourceCounter,
            [FromServices] ITargetCountExecutor targetCounter,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(schema)) return BadRequest("A schema name is required.");
            if (string.IsNullOrWhiteSpace(table)) return BadRequest("A table name is required.");

            var (conn, password, problem) = await OpenAsync(id, cancellationToken);
            if (problem != null) return problem;

            var isOracle = conn!.Kind == ConnectionKind.Oracle;
            var schemaName = isOracle ? SourceSchemaName(schema) : schema;

            long? rows = null;
            string? error = null;
            var clock = Stopwatch.StartNew();
            try
            {
                rows = isOracle
                    ? await sourceCounter.GetRowCountAsync(conn, password!, schemaName, table, null, cancellationToken)
                    : await targetCounter.GetRowCountAsync(conn, password!, schemaName, table, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Stopped by the user (or the page closed): not an error of the table, record nothing.
                return StatusCode(499);
            }
            catch (Exception ex)
            {
                error = FirstLine(ex.Message);
            }
            clock.Stop();

            var now = DateTimeOffset.UtcNow;
            var saved = await _db.TableRowCounts.FirstOrDefaultAsync(
                c => c.ConnectionId == id && c.SchemaName == schemaName && c.TableName == table, CancellationToken.None);
            if (saved == null && rows == null)
            {
                // A table the list never had, and it could not be counted (usually: it does not exist). Report
                // the error, but do not add a row for it to the comparison.
                return Ok(new TableCount(table, null, null, clock.ElapsedMilliseconds, error));
            }
            if (saved == null)
            {
                saved = new TableRowCount { ConnectionId = id, SchemaName = schemaName, TableName = table, ListedAt = now };
                _db.TableRowCounts.Add(saved);
            }
            saved.Error = error;
            saved.DurationMs = clock.ElapsedMilliseconds;
            if (rows.HasValue)
            {
                saved.Rows = rows;
                saved.CountedAt = now;
            }

            // Keep "Sync counts & sizes" on the migration pages in step: it shows the same exact count.
            if (isOracle && rows.HasValue)
            {
                var cached = await _db.DiscoveryCaches.FirstOrDefaultAsync(
                    c => c.ConnectionId == id && c.Owner == schemaName && c.TableName == table, CancellationToken.None);
                if (cached != null)
                {
                    cached.NumRows = rows;
                    cached.RowsCountedAt = now;
                }
            }

            await _db.SaveChangesAsync(CancellationToken.None);
            return Ok(ToCount(saved));
        }

        private async Task<List<TableRowCount>> SavedAsync(long connectionId, string schema, CancellationToken ct) =>
            await _db.TableRowCounts.AsNoTracking()
                .Where(c => c.ConnectionId == connectionId && c.SchemaName == schema)
                .ToListAsync(ct);

        private static TableCount ToCount(TableRowCount c) => new(c.TableName, c.Rows, c.CountedAt, c.DurationMs, c.Error);

        private static object SideInfo(List<TableRowCount> saved) => new
        {
            tables = saved.Count,
            listedAt = saved.Count == 0 ? (DateTimeOffset?)null : saved.Max(c => c.ListedAt),
            countedAt = saved.Where(c => c.CountedAt.HasValue).Select(c => c.CountedAt).DefaultIfEmpty().Max()
        };

        // Oracle stores unquoted owner names in upper case, and every other part of O2P addresses them so.
        private static string SourceSchemaName(string schema) => schema.Trim().ToUpperInvariant();

        private static string FirstLine(string message)
        {
            var line = message.Split('\n')[0].Trim();
            return line.Length > 500 ? line[..500] : line;
        }

        private async Task<(Connection? conn, string? password, IActionResult? problem)> OpenAsync(long id, CancellationToken ct)
        {
            var conn = await _db.Connections.FindAsync(new object[] { id }, ct);
            if (conn == null) return (null, null, BadRequest("That database does not exist."));
            try
            {
                return (conn, _secretProtector.Unprotect(conn.SecretCiphertext ?? Array.Empty<byte>()), null);
            }
            catch (Exception ex)
            {
                return (null, null, BadRequest($"Could not decrypt stored credentials for this connection: {ex.Message}. Re-enter the connection's password and try again."));
            }
        }
    }
}
