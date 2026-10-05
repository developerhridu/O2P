using O2P.Domain.Entities;
using O2P.Domain.Enums;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace O2P.Integration.Tests;

/// <summary>
/// A throwaway Oracle, described by environment variables. Every test here changes data in it, so it
/// must never be pointed at a real source.
/// </summary>
public sealed class OracleFixture
{
    public Connection Connection { get; }
    public string Password { get; }
    public string SystemPassword { get; }
    public string DataSource { get; }

    public OracleFixture()
    {
        DataSource = Required("O2P_IT_ORACLE");                 // host:port/service, e.g. 127.0.0.1:15210/FREEPDB1
        var user = Required("O2P_IT_ORACLE_USER");
        Password = Required("O2P_IT_ORACLE_PASSWORD");
        SystemPassword = Required("O2P_IT_ORACLE_SYSTEM_PASSWORD");

        var hostPort = DataSource.Split('/')[0];
        Connection = new Connection
        {
            Name = "integration-oracle",
            Kind = ConnectionKind.Oracle,
            Host = hostPort.Split(':')[0],
            Port = int.Parse(hostPort.Split(':')[1]),
            ServiceOrDb = DataSource.Split('/')[1],
            Username = user,
        };
    }

    public string User => Connection.Username.ToUpperInvariant();

    public OracleConnection Open(string? user = null, string? password = null)
    {
        var conn = new OracleConnection(new OracleConnectionStringBuilder
        {
            DataSource = DataSource,
            UserID = user ?? Connection.Username,
            Password = password ?? Password,
            Pooling = false
        }.ConnectionString);
        conn.Open();
        return conn;
    }

    public OracleConnection OpenSystem() => Open("system", SystemPassword);

    public static void Exec(OracleConnection conn, string sql)
    {
        using var cmd = new OracleCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public static void ExecIgnore(OracleConnection conn, string sql)
    {
        try { Exec(conn, sql); } catch (OracleException) { }
    }

    public static decimal CurrentScn(OracleConnection conn)
    {
        using var cmd = new OracleCommand("SELECT CURRENT_SCN FROM V$DATABASE", conn);
        return Convert.ToDecimal(cmd.ExecuteScalar());
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"{name} is not set. These tests need a throwaway Oracle; see O2P.Integration.Tests.csproj.");
}

[CollectionDefinition("oracle")]
public sealed class OracleCollection : ICollectionFixture<OracleFixture> { }
