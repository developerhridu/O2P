using Oracle.ManagedDataAccess.Client;

namespace O2P.Infrastructure.Oracle
{
    /// <summary>
    /// Creates every Oracle connection, with the settings that keep it alive through a load balancer,
    /// firewall or VPN - the Oracle counterpart of PostgresConnectionSettings.
    ///
    /// - TCP keepalive (probe after 20 s of silence, then every 5 s): a middlebox that drops idle sessions
    ///   never sees this one as idle, including while a long read waits on the writer. ODP.NET exposes it
    ///   only on the connection object, not in the connection string, hence a factory.
    /// - Validate Connection: a pooled session is checked with a round trip when it is handed out, and a
    ///   dead one is replaced instead of failing the first command with ORA-03113 / ORA-03135.
    /// - Min Pool Size 0: no session is kept open with nothing to do; idle sessions are the ones that get
    ///   cut.
    /// </summary>
    public static class OracleConnectionSettings
    {
        public const int KeepAliveTimeSeconds = 20;
        public const int KeepAliveIntervalSeconds = 5;

        public static OracleConnection Create(OracleConnectionStringBuilder builder)
        {
            if (builder.Pooling)
            {
                builder.ValidateConnection = true;
                builder.MinPoolSize = 0;
            }

            return new OracleConnection(builder.ConnectionString)
            {
                KeepAlive = true,
                KeepAliveTime = KeepAliveTimeSeconds,
                KeepAliveInterval = KeepAliveIntervalSeconds
            };
        }
    }
}
