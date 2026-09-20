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
            Exception? last = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await conn.OpenAsync(cancellationToken);
                    return;
                }
                catch (OracleException ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    last = ex;
                    // Transient only (timeout / busy). Non-transient errors fail immediately — no retry spam.
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }

            if (last != null) throw last;
            await conn.OpenAsync(cancellationToken);
        }

        private static bool IsTransient(OracleException ex)
        {
            // ORA-00018 max sessions, ORA-12535 / 12170 timeouts, ORA-03135 connection lost,
            // ORA-01089 shutdown, vendor-specific "connection request timed out" often surfaces as 50000.
            foreach (OracleError err in ex.Errors)
            {
                switch (err.Number)
                {
                    case 18:
                    case 12535:
                    case 12170:
                    case 3135:
                    case 1089:
                    case 12541:
                    case 12571:
                    case 50000:
                        return true;
                }
            }

            return false;
        }
    }
}
