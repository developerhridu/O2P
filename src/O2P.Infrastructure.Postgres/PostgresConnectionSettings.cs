using Npgsql;

namespace O2P.Infrastructure.Postgres
{
    /// <summary>
    /// Settings that keep a PostgreSQL connection alive through a load balancer, firewall or VPN.
    ///
    /// The destination (e.g. 172.16.10.58) is reached through a balancer that drops a TCP session it sees
    /// as idle in under a minute, and neither end notices: the server only probes after
    /// tcp_keepalives_idle (typically 7200 s), and Npgsql sends nothing by default. The pool then hands
    /// out sockets the balancer has already closed, and the first read fails with "Exception while
    /// reading from stream" / "An existing connection was forcibly closed by the remote host". The same
    /// lesson was learnt in biometric-app-api-core (PostgresDataManagerV2.Prepare).
    ///
    /// - KeepAlive: Npgsql sends a keepalive query every N seconds while the connection is idle, so an
    ///   idle pooled connection never looks idle to the balancer.
    /// - TCP keepalive: probes at the socket level. This is what protects a busy connection - during a
    ///   COPY Npgsql cannot send its keepalive query, but the socket can still go quiet for a long time
    ///   while the reader waits on Oracle.
    /// - ConnectionIdleLifetime: a pooled connection unused for 30 s is closed rather than kept for
    ///   Npgsql's default 300 s, far beyond what the balancer tolerates.
    ///
    /// Anything already set in a connection string wins, so a deployment can still tune it.
    /// </summary>
    public static class PostgresConnectionSettings
    {
        public const int KeepAliveSeconds = 20;
        public const int TcpKeepAliveTimeSeconds = 20;
        public const int TcpKeepAliveIntervalSeconds = 5;
        public const int IdleLifetimeSeconds = 30;

        public static NpgsqlConnectionStringBuilder Harden(NpgsqlConnectionStringBuilder builder)
        {
            if (builder.KeepAlive <= 0) builder.KeepAlive = KeepAliveSeconds;

            builder.TcpKeepAlive = true;
            if (builder.TcpKeepAliveTime <= 0) builder.TcpKeepAliveTime = TcpKeepAliveTimeSeconds;
            if (builder.TcpKeepAliveInterval <= 0) builder.TcpKeepAliveInterval = TcpKeepAliveIntervalSeconds;

            // 300 is Npgsql's default, i.e. "not chosen by anyone".
            if (builder.ConnectionIdleLifetime >= 300) builder.ConnectionIdleLifetime = IdleLifetimeSeconds;

            return builder;
        }

        /// <summary><see cref="Harden(NpgsqlConnectionStringBuilder)"/> for a connection string.</summary>
        public static string Harden(string connectionString) =>
            Harden(new NpgsqlConnectionStringBuilder(connectionString)).ConnectionString;
    }
}
