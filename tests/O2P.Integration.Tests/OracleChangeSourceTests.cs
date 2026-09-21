using Microsoft.Extensions.Logging.Abstractions;
using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Infrastructure.Oracle.ChangeTracking;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace O2P.Integration.Tests;

/// <summary>
/// The Oracle half of change tracking against a real database. Each test pins one claim the design
/// rests on - above all, that no committed change can slip between two copies.
/// </summary>
[Collection("oracle")]
public sealed class OracleChangeSourceTests
{
    private readonly OracleFixture _db;
    private readonly OracleChangeSource _source = new(NullLogger<OracleChangeSource>.Instance);

    public OracleChangeSourceTests(OracleFixture db) => _db = db;

    // ---- helpers ------------------------------------------------------------------------------

    private string NewTable(OracleConnection conn, string name, string columns)
    {
        OracleFixture.ExecIgnore(conn, $"DROP TABLE {name} PURGE");
        OracleFixture.Exec(conn, $"CREATE TABLE {name} ({columns})");

        // Oracle maps SCNs to time in steps of about three seconds, and reading a table AS OF an SCN
        // that close to its creation fails with ORA-01466. Real source tables are never that young.
        Thread.Sleep(TimeSpan.FromSeconds(6));
        return name.ToUpperInvariant();
    }

    private async Task<MiningTable> Tracked(string table, decimal fromScn, long id = 1)
    {
        var start = await _source.CaptureStartPointAsync(_db.Connection, _db.Password, _db.User, table, CancellationToken.None);
        Assert.True(start.Key != null, string.Join(" ", start.Problems));
        return new MiningTable(id, _db.User, table, start.Key!, start.ObjectIdsJson!, fromScn);
    }

    private async Task<MinedTable> MineOne(MiningTable table)
    {
        var marks = await _source.ReadScnMarksAsync(_db.Connection, _db.Password, CancellationToken.None);
        var mined = await _source.MineAsync(_db.Connection, _db.Password, new[] { table }, marks.S1, CancellationToken.None);
        return Assert.Single(mined);
    }

    private static HashSet<string> KeyTexts(MinedTable mined) => mined.Keys.Select(k => string.Join("|", k)).ToHashSet();

    // ---- start point --------------------------------------------------------------------------

    [Fact]
    public async Task A_start_point_records_the_key_the_logging_state_and_the_object_ids()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_start", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");

        var start = await _source.CaptureStartPointAsync(_db.Connection, _db.Password, _db.User, table, CancellationToken.None);

        Assert.True(start.CanTrack, string.Join(" ", start.Problems));
        Assert.Equal(new OracleKeyColumn("ID", "NUMBER"), Assert.Single(start.Key!));
        Assert.True(start.StartScn < OracleFixture.CurrentScn(conn));
        Assert.Contains("objectId", start.ObjectIdsJson);
    }

    [Fact]
    public async Task A_table_without_a_primary_key_cannot_be_tracked_and_says_why()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_nokey", "id NUMBER, name VARCHAR2(50)");

        var start = await _source.CaptureStartPointAsync(_db.Connection, _db.Password, _db.User, table, CancellationToken.None);

        Assert.False(start.CanTrack);
        Assert.Contains("no primary key", start.KeyProblem);
    }

    [Fact]
    public async Task An_open_transaction_holds_the_start_point_back_before_its_own_start()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_open", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");

        using var other = _db.Open();
        using var tx = other.BeginTransaction();
        OracleFixture.Exec(other, $"INSERT INTO {table} VALUES (1, 'uncommitted')");
        decimal txStart;
        using (var cmd = new OracleCommand(
            "SELECT t.start_scn FROM gv$transaction t JOIN gv$session s ON s.taddr = t.addr AND s.inst_id = t.inst_id WHERE s.sid = SYS_CONTEXT('USERENV', 'SID')", other))
        {
            txStart = Convert.ToDecimal(cmd.ExecuteScalar());
        }

        var start = await _source.CaptureStartPointAsync(_db.Connection, _db.Password, _db.User, table, CancellationToken.None);
        tx.Rollback();

        Assert.True(start.StartScn < txStart, $"start {start.StartScn} must be before the open transaction's start {txStart}");
    }

    // ---- mining -------------------------------------------------------------------------------

    [Fact]
    public async Task Mining_finds_the_keys_of_inserts_updates_deletes_and_key_changes()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_dml", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));

        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (1, 'a')");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (2, 'b')");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (3, 'c')");
        OracleFixture.Exec(conn, $"UPDATE {table} SET name = 'a2' WHERE id = 1");
        OracleFixture.Exec(conn, $"DELETE FROM {table} WHERE id = 2");
        OracleFixture.Exec(conn, $"UPDATE {table} SET id = 30 WHERE id = 3");

        var mined = await MineOne(tracked);

        Assert.Null(mined.StopReason);
        // 3 is the key-changing update's old key: without it the old row would linger in the destination.
        Assert.Equal(new HashSet<string> { "1", "2", "3", "30" }, KeyTexts(mined));
    }

    [Fact]
    public async Task Changes_at_or_before_the_tables_own_point_are_not_returned()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_before", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (1, 'old')");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (2, 'new')");

        Assert.Equal(new HashSet<string> { "2" }, KeyTexts(await MineOne(tracked)));
    }

    [Fact]
    public async Task A_change_made_only_through_a_LOB_is_found_from_where_the_row_lives()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_lob", "id NUMBER PRIMARY KEY, body CLOB");
        // Longer than 4000 bytes, so the LOB is stored out of line and written as a LOB, not an update.
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (7, TO_CLOB(RPAD('x', 4000, 'x')) || RPAD('y', 4000, 'y'))");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));

        OracleFixture.Exec(conn, $"DECLARE l CLOB; BEGIN SELECT body INTO l FROM {table} WHERE id = 7 FOR UPDATE; DBMS_LOB.WRITE(l, 3, 10, 'ZZZ'); COMMIT; END;");

        Assert.Equal(new HashSet<string> { "7" }, KeyTexts(await MineOne(tracked)));
    }

    [Fact]
    public async Task A_transaction_left_open_across_a_copy_is_mined_again_by_the_next_one()
    {
        // The case that decides whether updates can be lost. Session B changes a row and does not commit;
        // a copy runs; B commits; the next copy must still see that row, although its redo sits inside
        // the window the first copy already covered.
        using var conn = _db.Open();
        var table = NewTable(conn, "it_late", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (5, 'before')");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));

        using var other = _db.Open();
        using var tx = other.BeginTransaction();
        OracleFixture.Exec(other, $"UPDATE {table} SET name = 'after' WHERE id = 5");

        var first = await _source.ReadScnMarksAsync(_db.Connection, _db.Password, CancellationToken.None);
        Assert.NotNull(first.OldestOpenStart);
        await _source.MineAsync(_db.Connection, _db.Password, new[] { tracked }, first.S1, CancellationToken.None);

        tx.Commit();

        var next = tracked with { FromScn = first.ResumePoint };
        Assert.Contains("5", KeyTexts(await MineOne(next)));
    }

    [Fact]
    public async Task A_truncate_stops_the_table_rather_than_being_missed()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_trunc", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (1, 'a')");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));

        OracleFixture.Exec(conn, $"TRUNCATE TABLE {table}");

        var mined = await MineOne(tracked);
        Assert.Contains("truncated", mined.StopReason);
        Assert.Empty(mined.Keys);
    }

    [Fact]
    public async Task A_column_added_in_Oracle_stops_the_table()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_ddl", "id NUMBER PRIMARY KEY, name VARCHAR2(50)");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));

        OracleFixture.Exec(conn, $"ALTER TABLE {table} ADD (extra NUMBER)");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (1, 'a', 1)");

        Assert.Contains("structural change", (await MineOne(tracked)).StopReason);
    }

    [Fact]
    public async Task One_table_stopping_does_not_stop_the_others()
    {
        using var conn = _db.Open();
        var stopped = NewTable(conn, "it_mix_a", "id NUMBER PRIMARY KEY");
        var fine = NewTable(conn, "it_mix_b", "id NUMBER PRIMARY KEY");
        // A row, so the truncate below really removes something. Truncating a table that never held a
        // row changes nothing, and correctly does not stop tracking.
        OracleFixture.Exec(conn, $"INSERT INTO {stopped} VALUES (1)");
        OracleFixture.Exec(conn, "COMMIT");
        var from = OracleFixture.CurrentScn(conn);
        var a = await Tracked(stopped, from, id: 1);
        var b = await Tracked(fine, from, id: 2);

        OracleFixture.Exec(conn, $"TRUNCATE TABLE {stopped}");
        OracleFixture.Exec(conn, $"INSERT INTO {fine} VALUES (9)");
        OracleFixture.Exec(conn, "COMMIT");

        var marks = await _source.ReadScnMarksAsync(_db.Connection, _db.Password, CancellationToken.None);
        var mined = await _source.MineAsync(_db.Connection, _db.Password, new[] { a, b }, marks.S1, CancellationToken.None);

        Assert.NotNull(mined.Single(m => m.Id == 1).StopReason);
        Assert.Equal(new HashSet<string> { "9" }, KeyTexts(mined.Single(m => m.Id == 2)));
    }

    [Fact]
    public async Task History_that_is_gone_is_reported_not_skipped()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_gone", "id NUMBER PRIMARY KEY");
        var tracked = await Tracked(table, fromScn: 1000);

        var marks = await _source.ReadScnMarksAsync(_db.Connection, _db.Password, CancellationToken.None);
        await Assert.ThrowsAsync<ChangeHistoryGoneException>(() =>
            _source.MineAsync(_db.Connection, _db.Password, new[] { tracked }, marks.S1, CancellationToken.None));
    }

    // ---- reading rows back --------------------------------------------------------------------

    [Fact]
    public async Task Rows_are_read_back_by_key_with_the_filter_applied_and_deleted_keys_absent()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_rows", "id NUMBER PRIMARY KEY, status VARCHAR2(1), note VARCHAR2(20)");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (1, 'A', NULL)");
        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (2, 'B', 'filtered out')");
        OracleFixture.Exec(conn, "COMMIT");
        var asOf = OracleFixture.CurrentScn(conn);

        var columns = new[]
        {
            new TrackedColumn("ID", "NUMBER", "numeric", false),
            new TrackedColumn("STATUS", "VARCHAR2", "varchar(1)", true),
            new TrackedColumn("NOTE", "VARCHAR2", "varchar(20)", true),
        };
        await using var reader = await _source.OpenRowReaderAsync(_db.Connection, _db.Password, _db.User, table,
            columns, "status = 'A'", new[] { new OracleKeyColumn("ID", "NUMBER") }, asOf, CancellationToken.None);

        var rows = await reader.ReadAsync(new[] { new string?[] { "1" }, new string?[] { "2" }, new string?[] { "3" } }, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(1m, Convert.ToDecimal(row[0]));
        Assert.Equal(DBNull.Value, row[2]);
    }

    [Fact]
    public async Task Date_and_38_digit_number_keys_survive_the_trip_through_text()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_keys", "d DATE, n NUMBER(38), v VARCHAR2(10), CONSTRAINT it_keys_pk PRIMARY KEY (d, n)");
        var tracked = await Tracked(table, OracleFixture.CurrentScn(conn));

        OracleFixture.Exec(conn, $"INSERT INTO {table} VALUES (TO_DATE('2024-05-06 07:08:09', 'YYYY-MM-DD HH24:MI:SS'), 12345678901234567890123456789012345678, 'x')");
        OracleFixture.Exec(conn, "COMMIT");

        var mined = await MineOne(tracked);
        var key = Assert.Single(mined.Keys);
        Assert.Equal("2024-05-06 07:08:09", key[0]);
        Assert.Equal("12345678901234567890123456789012345678", key[1]);

        var columns = new[] { new TrackedColumn("V", "VARCHAR2", "varchar(10)", true) };
        await using var reader = await _source.OpenRowReaderAsync(_db.Connection, _db.Password, _db.User, table,
            columns, null, tracked.Key, OracleFixture.CurrentScn(conn), CancellationToken.None);

        Assert.Equal("x", Assert.Single(await reader.ReadAsync(mined.Keys.ToList(), CancellationToken.None))[0]);
    }

    [Fact]
    public async Task More_keys_than_one_batch_are_all_read()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_many", "id NUMBER PRIMARY KEY");
        OracleFixture.Exec(conn, $"INSERT INTO {table} SELECT LEVEL FROM dual CONNECT BY LEVEL <= 600");
        OracleFixture.Exec(conn, "COMMIT");

        await using var reader = await _source.OpenRowReaderAsync(_db.Connection, _db.Password, _db.User, table,
            new[] { new TrackedColumn("ID", "NUMBER", "numeric", false) }, null,
            new[] { new OracleKeyColumn("ID", "NUMBER") }, OracleFixture.CurrentScn(conn), CancellationToken.None);

        var keys = Enumerable.Range(1, 600).Select(i => (IReadOnlyList<string?>)new string?[] { i.ToString() }).ToList();
        Assert.Equal(600, (await reader.ReadAsync(keys, CancellationToken.None)).Count);
    }

    // ---- readiness ----------------------------------------------------------------------------

    [Fact]
    public async Task Readiness_passes_for_a_prepared_account()
    {
        using var conn = _db.Open();
        var table = NewTable(conn, "it_ready", "id NUMBER PRIMARY KEY");

        var result = await _source.CheckReadinessAsync(_db.Connection, _db.Password, new[] { (_db.User, table) }, CancellationToken.None);

        Assert.True(result.Ready, string.Join("\n", result.Items.Where(i => !i.Ok).Select(i => $"{i.Name}: {i.Detail}")
            .Concat(result.Tables.SelectMany(t => t.Problems))));
        Assert.Equal(MiningMode.PluggableDatabase, result.Mode);
    }

    [Fact]
    public async Task Readiness_lists_what_a_bare_account_lacks_with_the_sql_to_fix_it()
    {
        using (var sys = _db.OpenSystem())
        {
            OracleFixture.ExecIgnore(sys, "DROP USER it_bare CASCADE");
            OracleFixture.Exec(sys, "CREATE USER it_bare IDENTIFIED BY Bare2026 QUOTA UNLIMITED ON users");
            OracleFixture.Exec(sys, "GRANT CREATE SESSION, CREATE TABLE TO it_bare");
        }
        using (var bare = _db.Open("it_bare", "Bare2026"))
        {
            OracleFixture.Exec(bare, "CREATE TABLE nokey (id NUMBER)");
        }

        var bareConnection = new O2P.Domain.Entities.Connection
        {
            Name = "bare", Host = _db.Connection.Host, Port = _db.Connection.Port,
            ServiceOrDb = _db.Connection.ServiceOrDb, Username = "it_bare"
        };
        var result = await _source.CheckReadinessAsync(bareConnection, "Bare2026", new[] { ("IT_BARE", "NOKEY") }, CancellationToken.None);

        Assert.False(result.Ready);
        var logminer = result.Items.Single(i => i.Name == "Start a LogMiner session");
        Assert.False(logminer.Ok);
        Assert.Contains("GRANT LOGMINING TO IT_BARE", logminer.FixSql);
        Assert.Contains(result.Tables.Single().Problems, p => p.Contains("no primary key"));
    }
}
