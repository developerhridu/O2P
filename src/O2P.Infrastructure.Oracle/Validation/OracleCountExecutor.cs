using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.Validation
{
    public class OracleCountExecutor : ISourceCountExecutor
    {
        public async Task<long> GetRowCountAsync(Connection sourceConnection, string password, string owner, string tableName, string? whereClause, CancellationToken cancellationToken)
        {
            if (sourceConnection.Host.Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                // Same counts the mock discovery reports, so a mock scan and a mock count agree.
                await Task.Yield();
                return tableName.Equals("ORDERS", StringComparison.OrdinalIgnoreCase) ? 4500 : 1500;
            }

            var oracleCsb = new OracleConnectionStringBuilder
            {
                DataSource = $"{sourceConnection.Host}:{sourceConnection.Port}/{sourceConnection.ServiceOrDb}",
                UserID = sourceConnection.Username,
                Password = password,
                Pooling = true
            };

            using var oracleConn = new OracleConnection(oracleCsb.ConnectionString);
            await oracleConn.OpenAsync(cancellationToken);
            
            // Quote the identifiers to match how the reader addresses the same table (case-exact),
            // keeping the source count consistent with what was actually read and migrated.
            string sourceSql = $"SELECT COUNT(*) FROM {O2P.Application.Schema.SqlIdentifier.OracleQualified(owner, tableName)}";
            if (!string.IsNullOrEmpty(whereClause))
            {
                sourceSql += $" WHERE {whereClause}";
            }
            
            using var oracleCmd = oracleConn.CreateCommand();
            oracleCmd.CommandText = sourceSql;
            return Convert.ToInt64(await oracleCmd.ExecuteScalarAsync(cancellationToken));
        }
    }
}
