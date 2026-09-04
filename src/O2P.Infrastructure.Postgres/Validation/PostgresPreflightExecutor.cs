using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Npgsql;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Validation
{
    public class PostgresPreflightExecutor : ITargetPreflightExecutor
    {
        private string GetConnectionString(Connection conn, string password)
        {
            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = conn.Host,
                Port = conn.Port,
                Database = conn.ServiceOrDb,
                Username = conn.Username,
                Password = password,
                Pooling = false
            };
            return csb.ConnectionString;
        }

        public async Task<bool> CheckVersionAsync(Connection targetConnection, string password, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString(targetConnection, password));
                await conn.OpenAsync(cancellationToken);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT current_setting('server_version_num')";
                var versionStr = await cmd.ExecuteScalarAsync(cancellationToken) as string;
                
                if (int.TryParse(versionStr, out int versionNum))
                {
                    return versionNum >= 140000;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> CheckSchemaPrivilegesAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString(targetConnection, password));
                await conn.OpenAsync(cancellationToken);

                using var cmd = conn.CreateCommand();
                // Check if current user has USAGE and CREATE on the specified schema
                cmd.CommandText = $"SELECT has_schema_privilege('{schema}', 'USAGE, CREATE')";
                var hasPriv = await cmd.ExecuteScalarAsync(cancellationToken);
                return Convert.ToBoolean(hasPriv);
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> RunProbeTableAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString(targetConnection, password));
                await conn.OpenAsync(cancellationToken);

                string probeTableName = $"_o2p_probe_{Guid.NewGuid():N}";
                var qualifiedProbe = O2P.Application.Schema.SqlIdentifier.QuotePostgresQualified(schema, probeTableName);
                using var cmd = conn.CreateCommand();

                cmd.CommandText = $"CREATE TABLE {qualifiedProbe} (id int)";
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                cmd.CommandText = $"INSERT INTO {qualifiedProbe} VALUES (1)";
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                cmd.CommandText = $"DROP TABLE {qualifiedProbe}";
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
