using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using O2P.Domain.Entities;
using O2P.Domain.Enums;
using O2P.Infrastructure.Metadata;
using O2P.Application.Interfaces;
using Oracle.ManagedDataAccess.Client;
using Npgsql;
using O2P.Infrastructure.Oracle;
using O2P.Infrastructure.Postgres;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    //[Authorize(Roles = "Admin,Operator,Viewer")]
    public class ConnectionsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ISecretProtector _secretProtector;

        public ConnectionsController(AppDbContext db, ISecretProtector secretProtector)
        {
            _db = db;
            _secretProtector = secretProtector;
        }

        [HttpGet]
        public async Task<IActionResult> GetConnections()
        {
            var connections = await _db.Connections.ToListAsync();
            // Mask password byte array in response to prevent leakage
            foreach (var conn in connections)
            {
                conn.SecretCiphertext = System.Array.Empty<byte>();
            }
            return Ok(connections);
        }

        public class CreateConnectionRequest
        {
            public string Name { get; set; } = null!;
            public string Kind { get; set; } = null!; // "oracle" or "postgres"
            public string Host { get; set; } = null!;
            public int Port { get; set; }
            public string ServiceOrDb { get; set; } = null!;
            public string Username { get; set; } = null!;
            public string Password { get; set; } = null!;
            public string? OptionsJson { get; set; }
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> CreateConnection([FromBody] CreateConnectionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Host))
            {
                return BadRequest("A name and host are required.");
            }

            if (await _db.Connections.AnyAsync(c => c.Name == request.Name))
            {
                return Conflict($"A database named \"{request.Name}\" already exists. Choose a different name.");
            }

            var conn = new Connection
            {
                Name = request.Name,
                Kind = request.Kind.ToLower() == "oracle" ? ConnectionKind.Oracle : ConnectionKind.Postgres,
                Host = request.Host,
                Port = request.Port,
                ServiceOrDb = request.ServiceOrDb,
                Username = request.Username,
                SecretCiphertext = _secretProtector.Protect(request.Password),
                OptionsJson = request.OptionsJson,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Connections.Add(conn);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Conflict($"A database named \"{request.Name}\" already exists. Choose a different name.");
            }

            conn.SecretCiphertext = System.Array.Empty<byte>(); // Mask in response
            return CreatedAtAction(nameof(GetConnections), new { id = conn.Id }, conn);
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteConnection(long id)
        {
            var conn = await _db.Connections.FindAsync(id);
            if (conn == null) return NotFound();

            _db.Connections.Remove(conn);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        /// <summary>
        /// Schemas in a PostgreSQL destination that this account can use, for the destination
        /// picker. Restricted like the other endpoints that open a live session with stored
        /// credentials.
        /// </summary>
        [HttpGet("{id}/schemas")]
        [Authorize(Roles = "Admin,Operator")]
        public async Task<IActionResult> GetSchemas(
            long id,
            [FromServices] IPostgresSchemaInspector inspector,
            CancellationToken cancellationToken)
        {
            var conn = await _db.Connections.FindAsync(new object[] { id }, cancellationToken);
            if (conn == null || conn.Kind != O2P.Domain.Enums.ConnectionKind.Postgres)
                return BadRequest("That destination database does not exist.");

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
                var schemas = await inspector.ListSchemasAsync(conn, password, cancellationToken);
                return Ok(new { schemas = schemas.Select(s => new { name = s.Name, canCreate = s.CanCreate }) });
            }
            catch (System.Exception ex)
            {
                return BadRequest($"Could not read the schemas: {ex.Message}");
            }
        }

        [HttpPost("{id}/test")]
        public async Task<IActionResult> TestConnection(long id)
        {
            var conn = await _db.Connections.FindAsync(id);
            if (conn == null) return NotFound("Connection profile not found.");

            var latencyStart = DateTime.UtcNow;
            try
            {
                var password = _secretProtector.Unprotect(conn.SecretCiphertext ?? System.Array.Empty<byte>());
                if (conn.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(new
                    {
                        success = true,
                        latencyMs = 12.5,
                        serverVersion = conn.Kind == ConnectionKind.Oracle ? "Oracle Database 19c (Mock)" : "PostgreSQL 15.2 (Mock)",
                        privileges = new[]
                        {
                            new { name = "Establish Connection", ok = true, detail = "Mock Connection Succeeded" },
                            new { name = "Privilege Validation Check", ok = true, detail = "Mock Validation Succeeded" }
                        }
                    });
                }

                if (conn.Kind == ConnectionKind.Oracle)
                {
                    var csb = new OracleConnectionStringBuilder
                    {
                        DataSource = $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={conn.Host})(PORT={conn.Port}))(CONNECT_DATA=(SERVICE_NAME={conn.ServiceOrDb})))",
                        UserID = conn.Username,
                        Password = password,
                        ConnectionTimeout = 5
                    };

                    using var oracleConn = OracleConnectionSettings.Create(csb);
                    await oracleConn.OpenAsync();

                    var latencyMs = (DateTime.UtcNow - latencyStart).TotalMilliseconds;

                    // Fetch version info
                    var serverVersion = oracleConn.ServerVersion;

                    // Basic check on DBA views accessibility
                    bool hasDbaAccess = false;
                    try
                    {
                        using var cmd = oracleConn.CreateCommand();
                        cmd.CommandText = "SELECT COUNT(*) FROM DBA_TABLES WHERE ROWNUM = 1";
                        await cmd.ExecuteScalarAsync();
                        hasDbaAccess = true;
                    }
                    catch { }

                    return Ok(new
                    {
                        success = true,
                        latencyMs = Math.Round(latencyMs, 2),
                        serverVersion = $"Oracle Database {serverVersion}",
                        privileges = new[]
                        {
                            new { name = "Establish Connection", ok = true, detail = "Success" },
                            new { name = "DBA Catalog Views Access", ok = hasDbaAccess, detail = hasDbaAccess ? "Granted" : "Limited (Missing DBA_* grants)" }
                        }
                    });
                }
                else
                {
                    var csb = new NpgsqlConnectionStringBuilder
                    {
                        Host = conn.Host,
                        Port = conn.Port,
                        Database = conn.ServiceOrDb,
                        Username = conn.Username,
                        Password = password,
                        Timeout = 5,
                        CommandTimeout = 5
                    };

                    using var pgConn = new NpgsqlConnection(PostgresConnectionSettings.Harden(csb).ConnectionString);
                    await pgConn.OpenAsync();

                    var latencyMs = (DateTime.UtcNow - latencyStart).TotalMilliseconds;
                    var serverVersion = pgConn.PostgreSqlVersion.ToString();

                    // Probe temporary table creation and clean up
                    bool canCreate = false;
                    string privilegeDetail = "";
                    try
                    {
                        using var cmd = pgConn.CreateCommand();
                        cmd.CommandText = "CREATE TEMP TABLE _o2p_test_probe (id int); DROP TABLE _o2p_test_probe;";
                        await cmd.ExecuteNonQueryAsync();
                        canCreate = true;
                        privilegeDetail = "Temporary table creation successful.";
                    }
                    catch (Exception ex)
                    {
                        privilegeDetail = ex.Message;
                    }

                    return Ok(new
                    {
                        success = true,
                        latencyMs = Math.Round(latencyMs, 2),
                        serverVersion = $"PostgreSQL {serverVersion}",
                        privileges = new[]
                        {
                            new { name = "Establish Connection", ok = true, detail = "Success" },
                            new { name = "Table DDL & DML operations", ok = canCreate, detail = privilegeDetail }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    success = false,
                    latencyMs = Math.Round((DateTime.UtcNow - latencyStart).TotalMilliseconds, 2),
                    message = $"Could not connect: {ex.Message}",
                    serverVersion = "",
                    privileges = new[]
                    {
                        new { name = "Establish Connection", ok = false, detail = ex.Message }
                    }
                });
            }
        }

        public class UpdateConnectionRequest
        {
            public string Name { get; set; } = null!;
            public string Kind { get; set; } = null!; // "oracle" or "postgres"
            public string Host { get; set; } = null!;
            public int Port { get; set; }
            public string ServiceOrDb { get; set; } = null!;
            public string Username { get; set; } = null!;
            public string? Password { get; set; } // Nullable, if not updated
            public string? OptionsJson { get; set; }
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateConnection(long id, [FromBody] UpdateConnectionRequest request)
        {
            var conn = await _db.Connections.FindAsync(id);
            if (conn == null) return NotFound();

            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Host))
            {
                return BadRequest("A name and host are required.");
            }

            if (await _db.Connections.AnyAsync(c => c.Name == request.Name && c.Id != id))
            {
                return Conflict($"A database named \"{request.Name}\" already exists. Choose a different name.");
            }

            conn.Name = request.Name;
            conn.Kind = request.Kind.ToLower() == "oracle" ? ConnectionKind.Oracle : ConnectionKind.Postgres;
            conn.Host = request.Host;
            conn.Port = request.Port;
            conn.ServiceOrDb = request.ServiceOrDb;
            conn.Username = request.Username;

            if (!string.IsNullOrEmpty(request.Password))
            {
                conn.SecretCiphertext = _secretProtector.Protect(request.Password);
            }

            conn.OptionsJson = request.OptionsJson;
            conn.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Conflict($"A database named \"{request.Name}\" already exists. Choose a different name.");
            }

            conn.SecretCiphertext = System.Array.Empty<byte>(); // Mask in response
            return Ok(conn);
        }
    }
}
