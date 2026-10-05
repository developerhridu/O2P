using Npgsql;
using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Infrastructure.Postgres.Schema;
using O2P.Infrastructure.Postgres.Writer;
using Xunit;

namespace O2P.Integration.Tests;

/// <summary>The PostgreSQL half of change tracking, against a real database.</summary>
[Collection("postgres")]
public sealed class PostgresChangeApplierTests
{
    private const string S = PostgresFixture.Schema;
    private readonly PostgresFixture _db;
    private readonly PostgresChangeApplier _applier = new(new PostgresSchemaInspector());

    public PostgresChangeApplierTests(PostgresFixture db) => _db = db;

    private static readonly TrackedColumn Id = new("ID", "NUMBER", "bigint", false);
    private static readonly TrackedColumn Name = new("NAME", "VARCHAR2", "varchar(50)", true);
    private static readonly OracleKeyColumn IdKey = new("ID", "NUMBER");

    private ChangeTarget Target(string table, string? style = PostgresName.LowerStyle, TrackedColumn[]? columns = null, OracleKeyColumn[]? key = null) =>
        new(_db.Connection, _db.Password, S, table, style, columns ?? new[] { Id, Name }, key ?? new[] { IdKey });

    private static IReadOnlyList<string?>[] Keys(params string[] ids) => ids.Select(i => (IReadOnlyList<string?>)new string?[] { i }).ToArray();

    [Fact]
    public async Task Rows_that_exist_are_written_and_keys_without_a_row_are_deleted()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.apply1 (id bigint PRIMARY KEY, name varchar(50)); INSERT INTO {S}.apply1 VALUES (1, 'old'), (2, 'gone'), (9, 'untouched');");

        await using var session = await _applier.OpenAsync(Target("apply1"), settle: false, CancellationToken.None);
        var result = await session.ApplyAsync(Keys("1", "2", "3"),
            new[] { new object[] { 1m, "new" }, new object[] { 3m, "added" } }, CancellationToken.None);

        Assert.Equal(new ApplyResult(2, 1), result);
        Assert.Equal(new[] { "1|new", "3|added", "9|untouched" }, PostgresFixture.Rows(conn, $"SELECT id, name FROM {S}.apply1 ORDER BY id"));
    }

    [Fact]
    public async Task Applying_the_same_batch_again_changes_nothing()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.apply2 (id bigint PRIMARY KEY, name varchar(50));");

        await using var session = await _applier.OpenAsync(Target("apply2"), settle: false, CancellationToken.None);
        var rows = new[] { new object[] { 1m, "a" } };
        await session.ApplyAsync(Keys("1", "2"), rows, CancellationToken.None);
        var again = await session.ApplyAsync(Keys("1", "2"), rows, CancellationToken.None);

        // IS DISTINCT FROM: a row whose values did not change is not rewritten, so triggers stay quiet.
        Assert.Equal(new ApplyResult(0, 0), again);
        Assert.Equal(new[] { "1|a" }, PostgresFixture.Rows(conn, $"SELECT id, name FROM {S}.apply2"));
    }

    [Fact]
    public async Task A_table_without_a_usable_key_is_refused_with_the_fix()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.nokey (id bigint, name varchar(50)); CREATE UNIQUE INDEX nokey_partial ON {S}.nokey (id) WHERE id > 0;");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _applier.OpenAsync(Target("nokey"), settle: false, CancellationToken.None));
        Assert.Contains("ADD PRIMARY KEY (\"id\")", ex.Message);
    }

    [Fact]
    public async Task A_missing_destination_table_is_reported_plainly()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _applier.OpenAsync(Target("does_not_exist"), settle: false, CancellationToken.None));
        Assert.Contains("is not there any more", ex.Message);
    }

    [Fact]
    public async Task Settling_removes_a_row_the_load_copied_twice_then_adds_the_primary_key()
    {
        using var conn = _db.Open();
        // As a fuzzy bulk load can leave it: key 5 twice (changed during the load), no key constraint.
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.settle1 (id bigint NOT NULL, name varchar(50)); INSERT INTO {S}.settle1 VALUES (5, 'v1'), (5, 'v2'), (6, 'deleted in oracle'), (7, 'steady');");

        await using var session = await _applier.OpenAsync(Target("settle1"), settle: true, CancellationToken.None);
        var result = await session.ApplyAsync(Keys("5", "6"), new[] { new object[] { 5m, "final" } }, CancellationToken.None);
        var problem = await session.FinishSettlingAsync(CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(new[] { "5|final", "7|steady" }, PostgresFixture.Rows(conn, $"SELECT id, name FROM {S}.settle1 ORDER BY id"));
        Assert.Equal("settle1_pkey", PostgresFixture.Scalar(conn,
            $"SELECT conname FROM pg_constraint WHERE conrelid = '{S}.settle1'::regclass AND contype = 'p'"));
    }

    [Fact]
    public async Task Settling_refuses_to_add_a_key_while_duplicates_remain()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.settle2 (id bigint NOT NULL, name varchar(50)); INSERT INTO {S}.settle2 VALUES (8, 'a'), (8, 'b');");

        await using var session = await _applier.OpenAsync(Target("settle2"), settle: true, CancellationToken.None);
        await session.ApplyAsync(Keys("1"), Array.Empty<object[]>(), CancellationToken.None);

        Assert.Contains("more than one row", await session.FinishSettlingAsync(CancellationToken.None));
        Assert.Null(PostgresFixture.Scalar(conn, $"SELECT conname FROM pg_constraint WHERE conrelid = '{S}.settle2'::regclass AND contype = 'p'"));
    }

    [Fact]
    public async Task Identity_columns_keep_the_source_value_and_are_never_updated()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.ident (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name varchar(50));");

        await using var session = await _applier.OpenAsync(Target("ident"), settle: false, CancellationToken.None);
        await session.ApplyAsync(Keys("40"), new[] { new object[] { 40m, "first" } }, CancellationToken.None);
        await session.ApplyAsync(Keys("40"), new[] { new object[] { 40m, "second" } }, CancellationToken.None);

        Assert.Equal(new[] { "40|second" }, PostgresFixture.Rows(conn, $"SELECT id, name FROM {S}.ident"));
    }

    [Fact]
    public async Task A_table_an_earlier_run_created_in_upper_case_works_under_the_source_style()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.\"UPPER\" (\"ID\" bigint PRIMARY KEY, \"NAME\" varchar(50));");

        await using var session = await _applier.OpenAsync(Target("UPPER", PostgresName.SourceStyle), settle: false, CancellationToken.None);
        await session.ApplyAsync(Keys("1"), new[] { new object[] { 1m, "x" } }, CancellationToken.None);

        Assert.Equal(new[] { "1|x" }, PostgresFixture.Rows(conn, $"SELECT \"ID\", \"NAME\" FROM {S}.\"UPPER\""));
    }

    [Fact]
    public async Task Date_raw_and_38_digit_keys_match_rows_written_by_the_bulk_copy()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $@"
            CREATE TABLE {S}.keys3 (d timestamp(0) without time zone, r bytea, n numeric(38), v varchar(10), PRIMARY KEY (d, r, n));
            INSERT INTO {S}.keys3 VALUES ('2024-05-06 07:08:09', '\xCAFE', 12345678901234567890123456789012345678, 'old');");

        var columns = new[]
        {
            new TrackedColumn("D", "DATE", "timestamp(0) without time zone", false),
            new TrackedColumn("R", "RAW", "bytea", false),
            new TrackedColumn("N", "NUMBER", "numeric(38)", false),
            new TrackedColumn("V", "VARCHAR2", "varchar(10)", true),
        };
        var key = new[] { new OracleKeyColumn("D", "DATE"), new OracleKeyColumn("R", "RAW"), new OracleKeyColumn("N", "NUMBER") };

        await using var session = await _applier.OpenAsync(Target("keys3", columns: columns, key: key), settle: false, CancellationToken.None);
        // The key text exactly as LogMiner hands it out; the row deleted in Oracle, so no row comes back.
        var result = await session.ApplyAsync(
            new IReadOnlyList<string?>[] { new string?[] { "2024-05-06 07:08:09", "CAFE", "12345678901234567890123456789012345678" } },
            Array.Empty<object[]>(), CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0L, PostgresFixture.Scalar(conn, $"SELECT count(*) FROM {S}.keys3"));
    }

    [Fact]
    public async Task A_busy_row_makes_the_batch_fail_rather_than_wait_forever()
    {
        using var conn = _db.Open();
        PostgresFixture.Exec(conn, $"CREATE TABLE {S}.busy (id bigint PRIMARY KEY, name varchar(50)); INSERT INTO {S}.busy VALUES (1, 'a');");

        using var holder = _db.Open();
        using var tx = holder.BeginTransaction();
        using (var lockCmd = new NpgsqlCommand($"LOCK TABLE {S}.busy IN ACCESS EXCLUSIVE MODE", holder, tx)) lockCmd.ExecuteNonQuery();

        await using var session = await _applier.OpenAsync(Target("busy"), settle: false, CancellationToken.None);
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<PostgresException>(() => session.ApplyAsync(Keys("1"), new[] { new object[] { 1m, "b" } }, CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60));
        tx.Rollback();
    }
}
