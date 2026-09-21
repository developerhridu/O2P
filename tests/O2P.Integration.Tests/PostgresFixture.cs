using Npgsql;
using O2P.Domain.Entities;
using O2P.Domain.Enums;
using Xunit;

namespace O2P.Integration.Tests;

/// <summary>
/// A throwaway PostgreSQL, described by environment variables. The tests create and drop a schema of
/// their own in it, so it must never be a real destination.
/// </summary>
public sealed class PostgresFixture
{
    public const string Schema = "it_changes";

    public Connection Connection { get; }
    public string Password { get; }

    public PostgresFixture()
    {
        var target = Required("O2P_IT_PG");                   // host:port/database
        Password = Required("O2P_IT_PG_PASSWORD");
        var hostPort = target.Split('/')[0];
        if (hostPort.EndsWith(":5432"))
        {
            throw new InvalidOperationException("Refusing to run against port 5432: point O2P_IT_PG at the throwaway PostgreSQL.");
        }

        Connection = new Connection
        {
            Name = "integration-postgres",
            Kind = ConnectionKind.Postgres,
            Host = hostPort.Split(':')[0],
            Port = int.Parse(hostPort.Split(':')[1]),
            ServiceOrDb = target.Split('/')[1],
            Username = Required("O2P_IT_PG_USER"),
        };

        using var conn = Open();
        Exec(conn, $"DROP SCHEMA IF EXISTS {Schema} CASCADE; CREATE SCHEMA {Schema};");
    }

    public NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = Connection.Host,
            Port = Connection.Port,
            Database = Connection.ServiceOrDb,
            Username = Connection.Username,
            Password = Password,
            Pooling = false
        }.ConnectionString);
        conn.Open();
        return conn;
    }

    public static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        return cmd.ExecuteScalar();
    }

    public static List<string> Rows(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }
        return rows;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set. These tests need a throwaway PostgreSQL; see O2P.Integration.Tests.csproj.");
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
