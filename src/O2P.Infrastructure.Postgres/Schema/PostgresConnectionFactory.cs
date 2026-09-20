using Npgsql;
using O2P.Domain.Entities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Schema
{
    /// <summary>
    /// Shared connection handling for the Postgres schema classes, so the mock short-circuit and the
    /// connection-string shape are defined in exactly one place.
    /// </summary>
    internal static class PostgresConnectionFactory
    {
        public static bool IsMock(Connection connection) =>
            connection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase);

        public static async Task<NpgsqlConnection> OpenAsync(Connection connection, string password, CancellationToken cancellationToken)
        {
            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = connection.Host,
                Port = connection.Port,
                Database = connection.ServiceOrDb,
                Username = connection.Username,
                Password = password,
                Pooling = false
            };

            var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
    }
}
