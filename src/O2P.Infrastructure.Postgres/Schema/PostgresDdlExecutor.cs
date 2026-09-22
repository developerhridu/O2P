using Npgsql;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Schema
{
    public class PostgresDdlExecutor : IPostgresDdlExecutor
    {
        public async Task ExecuteDdlAsync(Connection connection, string password, string ddlScript, CancellationToken cancellationToken)
        {
            if (connection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase)) return;

            if (string.IsNullOrWhiteSpace(ddlScript)) return;

            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = connection.Host,
                Port = connection.Port,
                Database = connection.ServiceOrDb,
                Username = connection.Username,
                Password = password,
                Pooling = false
            };

            using var conn = new NpgsqlConnection(PostgresConnectionSettings.Harden(csb).ConnectionString);
            await conn.OpenAsync(cancellationToken);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = ddlScript;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
