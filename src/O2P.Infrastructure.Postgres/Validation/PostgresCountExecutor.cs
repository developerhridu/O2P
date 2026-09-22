using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Npgsql;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Validation
{
    public class PostgresCountExecutor : ITargetCountExecutor
    {
        public async Task<long> GetRowCountAsync(Connection targetConnection, string password, string schema, string tableName, CancellationToken cancellationToken)
        {
            if (targetConnection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                // Same figures as the mock Oracle count, so a mock comparison shows matching tables.
                await Task.Yield();
                return tableName.Equals("orders", StringComparison.OrdinalIgnoreCase) ? 4500 : 1500;
            }

            var pgCsb = new NpgsqlConnectionStringBuilder
            {
                Host = targetConnection.Host,
                Port = targetConnection.Port,
                Database = targetConnection.ServiceOrDb,
                Username = targetConnection.Username,
                Password = password,
                Pooling = true
            };

            using var pgConn = new NpgsqlConnection(PostgresConnectionSettings.Harden(pgCsb).ConnectionString);
            await pgConn.OpenAsync(cancellationToken);

            // Target tables are created with quoted, case-preserving identifiers, so the count must
            // also quote them - an unquoted "FROM public.CUSTOMERS" folds to lowercase and fails.
            string targetSql = $"SELECT COUNT(*) FROM {O2P.Application.Schema.SqlIdentifier.QuotePostgresQualified(schema, tableName)}";
            using var pgCmd = pgConn.CreateCommand();
            pgCmd.CommandText = targetSql;
            // No time limit: an exact count of a large table can take minutes, and the default 30 s failed it.
            // The caller's cancellation (the Stop button, a closed request) still cancels the query.
            pgCmd.CommandTimeout = 0;

            return Convert.ToInt64(await pgCmd.ExecuteScalarAsync(cancellationToken));
        }
    }
}
