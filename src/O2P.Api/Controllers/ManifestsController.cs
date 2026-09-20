using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
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
                    Included = true // Include by default
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
