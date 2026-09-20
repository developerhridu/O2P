using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using O2P.Application.Interfaces;
using O2P.Infrastructure.Metadata;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/connections/{connectionId}/[controller]")]
    //[Authorize(Roles = "Admin,Operator,Viewer")]
    public class DiscoveryController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IOracleDiscoveryService _discoveryService;
        private readonly ISecretProtector _secretProtector;

        public DiscoveryController(AppDbContext db, IOracleDiscoveryService discoveryService, ISecretProtector secretProtector)
        {
            _db = db;
            _discoveryService = discoveryService;
            _secretProtector = secretProtector;
        }

        public class RefreshDiscoveryRequest
        {
            // When null/empty, does a full schema scan (existing behavior, replaces the whole
            // owner's cache). When populated, does a targeted lookup for just these table names
            // and only upserts those rows, leaving the rest of the owner's cache untouched.
            public List<string>? TableNames { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> GetCachedTables(long connectionId, [FromQuery] string owner)
        {
            if (string.IsNullOrEmpty(owner)) return BadRequest("A source schema name is required.");

            var cached = await _db.DiscoveryCaches
                .Include(c => c.Columns)
                .Where(c => c.ConnectionId == connectionId && c.Owner == owner.ToUpper())
                .ToListAsync();

            return Ok(cached.Select(ToManifestReadyTable));
        }

        /// <summary>
        /// The schemas the source account can read tables from, for the UI's schema picker.
        /// Opens a live Oracle session with the stored credentials, so it is restricted to the same
        /// roles as <see cref="RefreshDiscovery"/>, which is the only thing that consumes the answer.
        /// </summary>
        [HttpGet("schemas")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> GetSchemas(long connectionId, CancellationToken cancellationToken)
        {
            var conn = await _db.Connections.FindAsync(new object[] { connectionId }, cancellationToken);
            if (conn == null || conn.Kind != O2P.Domain.Enums.ConnectionKind.Oracle)
                return BadRequest("That database does not exist.");

            string password;
            try
            {
                password = _secretProtector.Unprotect(conn.SecretCiphertext ?? System.Array.Empty<byte>());
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not decrypt stored credentials for this connection: {ex.Message}. Re-enter the connection's password and try again.");
            }

            try
            {
                var result = await _discoveryService.ListSchemasAsync(conn, password, cancellationToken);
                return Ok(new
                {
                    schemas = result.Schemas.Select(s => new { name = s.Name, tableCount = s.TableCount }),
                    skipped = result.Skipped
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not read the schemas: {ex.Message}");
            }
        }

        /// <summary>
        /// Brings one table's figures up to date: an exact COUNT(*) plus its current size. One table
        /// per call so the UI can show progress and stop part-way; a batch over a whole schema would
        /// risk an HTTP timeout and could not be interrupted.
        /// </summary>
        [HttpPost("sync")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> SyncTable(
            long connectionId,
            [FromQuery] string owner,
            [FromQuery] string table,
            [FromServices] ISourceCountExecutor countExecutor,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(owner)) return BadRequest("A source schema name is required.");
            if (string.IsNullOrWhiteSpace(table)) return BadRequest("A table name is required.");

            var conn = await _db.Connections.FindAsync(new object[] { connectionId }, cancellationToken);
            if (conn == null || conn.Kind != O2P.Domain.Enums.ConnectionKind.Oracle)
                return BadRequest("That database does not exist.");

            string password;
            try
            {
                password = _secretProtector.Unprotect(conn.SecretCiphertext ?? System.Array.Empty<byte>());
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not decrypt stored credentials for this connection: {ex.Message}. Re-enter the connection's password and try again.");
            }

            var ownerUpper = owner.ToUpper();
            var tableUpper = table.ToUpper();

            long rows;
            try
            {
                // Counts the whole table, ignoring any row filter, so the number means the same
                // thing as the Size column: how big the source table is.
                rows = await countExecutor.GetRowCountAsync(conn, password, ownerUpper, tableUpper, null, cancellationToken);
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not count {tableUpper}: {ex.Message}");
            }

            // The size is refreshed too, but a table that cannot be measured must not fail the sync -
            // the count is still worth keeping.
            TableSize size;
            try
            {
                size = await _discoveryService.GetTableSizeAsync(conn, password, ownerUpper, tableUpper, cancellationToken);
            }
            catch
            {
                size = new TableSize(null, false);
            }

            var countedAt = System.DateTimeOffset.UtcNow;
            var cached = await _db.DiscoveryCaches
                .FirstOrDefaultAsync(c => c.ConnectionId == connectionId && c.Owner == ownerUpper && c.TableName == tableUpper, cancellationToken);
            if (cached != null)
            {
                cached.NumRows = rows;
                cached.RowsCountedAt = countedAt;
                if (size.Bytes.HasValue)
                {
                    cached.SegmentBytes = size.Bytes;
                    cached.SizeIsEstimate = size.IsEstimate;
                }
                cached.LastRefreshedAt = countedAt;
                await _db.SaveChangesAsync(cancellationToken);
            }

            return Ok(new
            {
                owner = ownerUpper,
                tableName = tableUpper,
                rows,
                rowsCountedAt = countedAt,
                bytes = size.Bytes,
                sizeIsEstimate = size.IsEstimate
            });
        }

        [HttpPost("refresh")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> RefreshDiscovery(long connectionId, [FromQuery] string owner, [FromBody] RefreshDiscoveryRequest? request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(owner)) return BadRequest("A source schema name is required.");

            var conn = await _db.Connections.FindAsync(new object[] { connectionId }, cancellationToken);
            if (conn == null || conn.Kind != O2P.Domain.Enums.ConnectionKind.Oracle)
                return BadRequest("That database does not exist.");

            string password;
            try
            {
                password = _secretProtector.Unprotect(conn.SecretCiphertext ?? System.Array.Empty<byte>());
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not decrypt stored credentials for this connection: {ex.Message}. Re-enter the connection's password and try again.");
            }

            var requestedTableNames = request?.TableNames?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            IEnumerable<O2P.Domain.Entities.DiscoveryCache> tables;
            try
            {
                tables = await _discoveryService.DiscoverTablesAsync(conn, password, owner, cancellationToken, requestedTableNames);
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not scan the Oracle database: {ex.Message}");
            }

            var tablesList = tables.ToList();

            try
            {
                if (requestedTableNames == null || requestedTableNames.Count == 0)
                {
                    // Full schema scan: replace the whole owner's cache.
                    var oldCache = await _db.DiscoveryCaches
                        .Where(c => c.ConnectionId == connectionId && c.Owner == owner.ToUpper())
                        .ToListAsync(cancellationToken);
                    _db.DiscoveryCaches.RemoveRange(oldCache);
                }
                else
                {
                    // Targeted lookup: only replace cache rows for the requested tables.
                    var normalized = requestedTableNames.Select(t => t.Trim().ToUpperInvariant()).ToList();
                    var oldCache = await _db.DiscoveryCaches
                        .Where(c => c.ConnectionId == connectionId && c.Owner == owner.ToUpper() && normalized.Contains(c.TableName))
                        .ToListAsync(cancellationToken);
                    _db.DiscoveryCaches.RemoveRange(oldCache);
                }

                // Deletes are saved before inserts on purpose: EF Core's default save order runs
                // inserts before deletes for unrelated rows, which would trip the unique index on
                // (ConnectionId, Owner, TableName) when re-scanning tables already in the cache.
                await _db.SaveChangesAsync(cancellationToken);

                _db.DiscoveryCaches.AddRange(tablesList);
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return BadRequest($"Could not save the tables that were found: {ex.Message}");
            }

            return Ok(new
            {
                message = $"Found {tablesList.Count} table(s).",
                tables = tablesList.Select(ToManifestReadyTable)
            });
        }

        // Shapes a DiscoveryCache row into a manifest-table-ready DTO: the frontend can drop this
        // directly into a manifest's table list without needing to know Oracle->Postgres type
        // mapping rules itself.
        private static object ToManifestReadyTable(O2P.Domain.Entities.DiscoveryCache cache)
        {
            return new
            {
                owner = cache.Owner,
                tableName = cache.TableName,
                included = true,
                // Null means "Oracle does not know", not zero. The UI shows "Unknown".
                estRows = cache.NumRows,
                estBytes = cache.SegmentBytes,
                sizeIsEstimate = cache.SizeIsEstimate,
                rowsCountedAt = cache.RowsCountedAt,
                hasLobs = cache.LobBytes.HasValue && cache.LobBytes.Value > 0,
                isPartitioned = cache.IsPartitioned,
                isIot = cache.IsIot,
                whereClause = (string?)null,
                columns = cache.Columns.Select(col => new
                {
                    columnName = col.ColumnName,
                    oracleDataType = col.DataType,
                    postgresDataType = O2P.Application.Schema.TypeMapper.MapOracleToPostgres(col.DataType, col.DataLength, col.DataPrecision, col.DataScale),
                    isNullable = col.IsNullable,
                    isPrimaryKey = false,
                    isExcluded = false
                })
            };
        }
    }
}
