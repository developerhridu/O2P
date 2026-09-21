using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using O2P.Api.Services;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/")]
    //[Authorize(Roles = "Admin,Operator,Viewer")]
    public class ManifestsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public ManifestsController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet("applications/{appId}/manifests")]
        public async Task<IActionResult> GetManifests(long appId)
        {
            var manifests = await _db.Manifests
                .Where(m => m.ApplicationId == appId)
                .OrderByDescending(m => m.Version)
                .ToListAsync();
            return Ok(manifests);
        }

        [HttpPost("applications/{appId}/manifests")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> CreateManifest(long appId, [FromBody] Manifest manifest)
        {
            manifest.ApplicationId = appId;
            _db.Manifests.Add(manifest);
            await _db.SaveChangesAsync();
            return Ok(manifest);
        }

        [HttpGet("manifests/{id}")]
        public async Task<IActionResult> GetManifest(long id)
        {
            var manifest = await _db.Manifests
                .Include(m => m.Tables).ThenInclude(t => t.Columns)
                .Include(m => m.Tables).ThenInclude(t => t.Indexes)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (manifest == null) return NotFound();
            return Ok(manifest);
        }

        public class RenameManifestRequest
        {
            public string? Name { get; set; }
        }

        /// <summary>Renames a table selection. Its tables, and the runs made from it, are untouched.</summary>
        [HttpPatch("manifests/{id}")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> RenameManifest(long id, [FromBody] RenameManifestRequest request)
        {
            var manifest = await _db.Manifests.FirstOrDefaultAsync(m => m.Id == id);
            if (manifest == null) return NotFound();

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return BadRequest("Give the table selection a name.");
            if (name.Length > 200) return BadRequest("That name is too long; keep it under 200 characters.");

            // Names must be unique within a migration (and version) - the database enforces it too, but
            // with an error nobody could read.
            var lower = name.ToLower();
            if (await _db.Manifests.AnyAsync(m => m.Id != id && m.ApplicationId == manifest.ApplicationId
                                                  && m.Version == manifest.Version && m.Name.ToLower() == lower))
            {
                return Conflict($"This migration already has a table selection called \"{name}\". Choose a different name.");
            }

            manifest.Name = name;
            await _db.SaveChangesAsync();
            return Ok(manifest);
        }

        /// <summary>
        /// Deletes a table selection and the history of every run made from it (the database would cascade
        /// the runs anyway; this removes what it does not). Tables already copied into the destination, and
        /// their change tracking, are left alone.
        /// </summary>
        [HttpDelete("manifests/{id}")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> DeleteManifest(long id)
        {
            if (!await _db.Manifests.AnyAsync(m => m.Id == id)) return NotFound();

            var runs = _db.JobRuns.Where(j => j.ManifestId == id).Select(j => j.Id);
            var blocked = await RunHistory.WhyNotDeletableAsync(_db, runs);
            if (blocked != null) return Conflict(blocked);

            await using var tx = await _db.Database.BeginTransactionAsync();
            await RunHistory.DeleteAsync(_db, runs);
            await _db.Manifests.Where(m => m.Id == id).ExecuteDeleteAsync(); // tables and columns cascade
            await tx.CommitAsync();
            return NoContent();
        }

        [HttpPut("manifests/{id}/tables")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> UpdateManifestTables(long id, [FromBody] List<ManifestTable> tables)
        {
            var existingTables = await _db.ManifestTables.Where(t => t.ManifestId == id).ToListAsync();
            _db.ManifestTables.RemoveRange(existingTables);

            foreach (var t in tables)
            {
                t.Id = 0; // ensure new insertion
                t.ManifestId = id;
                _db.ManifestTables.Add(t);
            }

            await _db.SaveChangesAsync();
            return Ok();
        }

        [HttpPost("applications/{appId}/manifests/generate")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> GenerateManifest(long appId, [FromQuery] long connectionId, [FromQuery] string owner, [FromQuery] string version = "1.0")
        {
            var cached = await _db.DiscoveryCaches
                .Include(c => c.Columns)
                .Where(c => c.ConnectionId == connectionId && c.Owner == owner.ToUpper())
                .ToListAsync();

            if (!cached.Any()) return BadRequest("No tables have been scanned for that database and schema yet.");

            var manifest = new Manifest
            {
                ApplicationId = appId,
                Name = $"{owner} Schema Manifest v{version}",
                Version = 1,
                CreatedAt = System.DateTimeOffset.UtcNow
            };

            foreach (var cache in cached)
            {
                var table = new ManifestTable
                {
                    Owner = cache.Owner,
                    TableName = cache.TableName,
                    Included = true, // Include by default
                    // Carry the scan's statistics across. Without this the generated selection shows
                    // "Unknown" for every table even where the scan did find figures.
                    EstRows = cache.NumRows,
                    EstBytes = cache.SegmentBytes,
                    SizeIsEstimate = cache.SizeIsEstimate,
                    RowsCountedAt = cache.RowsCountedAt,
                    HasLobs = cache.LobBytes.HasValue && cache.LobBytes.Value > 0,
                    IsPartitioned = cache.IsPartitioned,
                    IsIot = cache.IsIot
                };

                foreach (var col in cache.Columns)
                {
                    table.Columns.Add(new ManifestColumn
                    {
                        ColumnName = col.ColumnName,
                        OracleDataType = col.DataType,
                        PostgresDataType = O2P.Application.Schema.TypeMapper.MapOracleToPostgres(col.DataType, col.DataLength, col.DataPrecision, col.DataScale),
                        IsNullable = col.IsNullable,
                        IsPrimaryKey = false, // Simplified for M3 initial implementation
                        IsExcluded = false
                    });
                }
                manifest.Tables.Add(table);
            }

            _db.Manifests.Add(manifest);
            await _db.SaveChangesAsync();

            return Ok(manifest);
        }
    }
}
