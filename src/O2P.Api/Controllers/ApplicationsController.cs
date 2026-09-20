using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Domain.Entities;
using O2P.Infrastructure.Metadata;
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

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteApplication(long id)
        {
            var app = await _db.Applications
                .Include(a => a.Connections)
                .FirstOrDefaultAsync(a => a.Id == id);
            if (app == null) return NotFound();

            // Fetch and remove associated manifests
            var manifests = await _db.Manifests.Where(m => m.ApplicationId == id).ToListAsync();
            _db.Manifests.RemoveRange(manifests);

            // Fetch and remove associated job runs
            var jobs = await _db.JobRuns.Where(j => j.ApplicationId == id).ToListAsync();
            _db.JobRuns.RemoveRange(jobs);

            _db.Applications.Remove(app);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
