using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Api.Services;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    //[Authorize(Roles = "Admin,Operator,Viewer")]
    public class ApplicationsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public ApplicationsController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> GetApplications()
        {
            var apps = await _db.Applications.Include(a => a.Connections).ToListAsync();
            return Ok(apps);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetApplication(long id)
        {
            var app = await _db.Applications.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();
            return Ok(app);
        }

        [HttpPost]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> CreateApplication([FromBody] O2P.Domain.Entities.Application app)
        {
            _db.Applications.Add(app);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetApplication), new { id = app.Id }, app);
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> UpdateApplication(long id, [FromBody] O2P.Domain.Entities.Application updatedApp)
        {
            var app = await _db.Applications.Include(a => a.Connections).FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();

            app.Name = updatedApp.Name;
            app.Description = updatedApp.Description;
            app.DefaultsJson = updatedApp.DefaultsJson;
            
            // Basic connection slot update
            _db.ApplicationConnections.RemoveRange(app.Connections);
            foreach (var conn in updatedApp.Connections)
            {
                conn.ApplicationId = id;
                _db.ApplicationConnections.Add(conn);
            }

            await _db.SaveChangesAsync();
            return Ok(app);
        }

        public class RenameRequest
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
        }

        /// <summary>
        /// Changes a migration's name and description only. The full update above also replaces the
        /// database choices with whatever is sent, so it is the wrong tool for a rename.
        /// </summary>
        [HttpPatch("{id}")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> RenameApplication(long id, [FromBody] RenameRequest request)
        {
            var app = await _db.Applications.FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return BadRequest("Give the migration a name.");
            if (name.Length > 200) return BadRequest("That name is too long; keep it under 200 characters.");

            // Case-insensitive on purpose: two migrations differing only in case would be told apart by
            // nobody, and the live-destination phrase ("MIGRATE <name> LIVE") would be ambiguous.
            var lower = name.ToLower();
            if (await _db.Applications.AnyAsync(a => a.Id != id && a.Name.ToLower() == lower))
            {
                return Conflict($"Another migration is already called \"{name}\". Choose a different name.");
            }

            app.Name = name;
            app.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            app.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(app);
        }

        /// <summary>
        /// Deletes a migration with its table selections, run history and database choices. The databases
        /// themselves, the tables already copied into the destination, and change-tracking state (which
        /// belongs to the destination table, not the migration) are left alone.
        /// </summary>
        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteApplication(long id)
        {
            if (!await _db.Applications.AnyAsync(a => a.Id == id)) return NotFound();

            var runs = _db.JobRuns.Where(j => j.ApplicationId == id).Select(j => j.Id);
            var blocked = await RunHistory.WhyNotDeletableAsync(_db, runs);
            if (blocked != null) return Conflict(blocked);

            await using var tx = await _db.Database.BeginTransactionAsync();
            await RunHistory.DeleteAsync(_db, runs);
            // Table selections (with their tables and columns) and the database choices cascade.
            await _db.Applications.Where(a => a.Id == id).ExecuteDeleteAsync();
            await tx.CommitAsync();
            return NoContent();
        }
    }
}
