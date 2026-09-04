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
