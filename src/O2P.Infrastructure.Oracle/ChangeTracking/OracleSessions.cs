using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    /// <summary>
    /// Sessions for change tracking. Always unpooled: the bulk reader and planner deliberately share one
    /// ODP.NET pool, and a change-tracking session alters its NLS formats and holds LogMiner state. Handed
    /// back to that pool, either would leak into a bulk read - an implicit date conversion in someone's row
    /// filter would silently change meaning.
    /// </summary>
    internal static class OracleSessions
    {
        public static bool IsMock(Connection connection) =>
            connection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase);

        public static async Task<OracleConnection> OpenUnpooledAsync(Connection connection, string password, CancellationToken ct)
        {
            var csb = new OracleConnectionStringBuilder
            {
                DataSource = $"{connection.Host}:{connection.Port}/{connection.ServiceOrDb}",
                UserID = connection.Username,
                Password = password,
                Pooling = false,
                ConnectionTimeout = 60
            };

            var conn = OracleConnectionSettings.Create(csb);
            try
            {
                await OracleConnectionRetry.OpenWithRetryAsync(conn, ct);
                return conn;
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
    }
}
