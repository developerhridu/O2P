using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace O2P.Infrastructure.Oracle.Validation
{
    public class OraclePreflightExecutor : ISourcePreflightExecutor
    {
        private readonly ILogger<OraclePreflightExecutor> _logger;

        public OraclePreflightExecutor(ILogger<OraclePreflightExecutor> logger)
        {
            _logger = logger;
        }

        public async Task<bool> CheckPrivilegesAsync(Connection sourceConnection, string password, CancellationToken cancellationToken)
        {
            try
            {
                var oracleCsb = new OracleConnectionStringBuilder
                {
                    DataSource = $"{sourceConnection.Host}:{sourceConnection.Port}/{sourceConnection.ServiceOrDb}",
                    UserID = sourceConnection.Username,
                    Password = password,
                    Pooling = false
                };

                using var conn = new OracleConnection(oracleCsb.ConnectionString);
                await conn.OpenAsync(cancellationToken);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM SESSION_PRIVS WHERE PRIVILEGE IN ('SELECT ANY TABLE', 'SELECT ANY DICTIONARY')";
                Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

                // Return true as long as we could connect and query SESSION_PRIVS.
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Oracle privilege check failed for {Host}:{Port}/{Service} user {User}",
                    sourceConnection.Host, sourceConnection.Port, sourceConnection.ServiceOrDb, sourceConnection.Username);
                return false;
            }
        }
    }
}
