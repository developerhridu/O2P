using Oracle.ManagedDataAccess.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle
{
    // Opening a fresh OracleConnection can transiently fail (ORA-50000 connection request
    // timed out, listener busy, brief network blips) when many workers are contending for a
    // limited number of sessions on a constrained source instance. A single failed attempt
    // shouldn't permanently fail an entire table - retry a few times with backoff first.
    internal static class OracleConnectionRetry
    {
        public static async Task OpenWithRetryAsync(OracleConnection conn, CancellationToken cancellationToken, int maxAttempts = 4)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await conn.OpenAsync(cancellationToken);
                    return;
                }
                catch (OracleException) when (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }
        }
    }
}
