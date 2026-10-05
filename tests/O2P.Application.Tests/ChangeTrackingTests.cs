using System.Text.Json;
using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using Xunit;

namespace O2P.Application.Tests;

public class ChangeWindowTests
{
    [Fact]
    public void With_nothing_open_the_resume_point_is_just_before_S0() =>
        Assert.Equal(999m, ChangeWindow.ResumePoint(s0: 1000m, oldestOpenStart: null));

    [Fact]
    public void An_open_transaction_older_than_S0_holds_the_resume_point_back_before_its_start() =>
        // Its changes sit between 500 and S1; the next copy must mine them again once it commits.
        Assert.Equal(499m, ChangeWindow.ResumePoint(s0: 1000m, oldestOpenStart: 500m));

    [Fact]
    public void A_transaction_that_opened_after_S0_does_not_move_the_resume_point_forward() =>
        // Started at 1005, after S0: its changes are after S0 anyway, so S0 - 1 already covers them.
        Assert.Equal(999m, ChangeWindow.ResumePoint(s0: 1000m, oldestOpenStart: 1005m));

    [Fact]
    public void One_mining_pass_starts_where_the_furthest_behind_table_is() =>
        Assert.Equal(40m, ChangeWindow.MiningStart(new[] { 90m, 40m, 70m }));

    [Fact]
    public void Scns_beyond_48_bits_are_not_rounded() =>
        // Oracle documents SCNs growing past 2^48; decimal holds them exactly, a double would not.
        Assert.Equal(281474976710656m, ChangeWindow.ResumePoint(281474976710657m, null));
}

public class TrackedTableSetupTests
{
    private static TableRun FinishedBulkCopy(Action<TableRun>? change = null)
    {
        var table = new ManifestTable { Owner = "HR", TableName = "EMPLOYEES", WhereClause = "  " };
        table.Columns.Add(new ManifestColumn { Id = 2, ColumnName = "NAME", OracleDataType = "VARCHAR2", PostgresDataType = "varchar(50)", IsNullable = true });
        table.Columns.Add(new ManifestColumn { Id = 1, ColumnName = "ID", OracleDataType = "NUMBER", PostgresDataType = "bigint", IsNullable = false });
        table.Columns.Add(new ManifestColumn { Id = 3, ColumnName = "SECRET", OracleDataType = "VARCHAR2", PostgresDataType = "text", IsExcluded = true });

        var run = new TableRun
        {
            Id = 77,
            ManifestTable = table,
            TargetTableName = "employees",
            TargetNameStyle = "lower",
            TargetTablePreExisted = false,
            SourceStartScn = 5000m,
            LoggingReadyAtStart = true,
            SourceKeyJson = JsonSerializer.Serialize(new[] { new OracleKeyColumn("ID", "NUMBER") }, TrackedTableSetup.Json),
            SourceObjectIdsJson = "[{\"objectId\":1}]",
        };
        change?.Invoke(run);
        return run;
    }

    [Fact]
    public void A_finished_copy_of_a_new_table_needs_a_first_sync_to_settle_it()
    {
        var (tracked, reason) = TrackedTableSetup.FromBulkCopy(FinishedBulkCopy(), 1, 2, "hr", DateTimeOffset.UnixEpoch);

        Assert.Null(reason);
        Assert.Equal(TrackedTableStatus.NeedsFirstSync, tracked!.Status);
        Assert.Equal(5000m, tracked.LastScn);
        Assert.Equal("employees", tracked.TargetTableName);
        Assert.Null(tracked.WhereClause); // blank filter means none
    }

    [Fact]
    public void A_copy_into_a_table_that_already_existed_is_ready_straight_away()
    {
        var (tracked, _) = TrackedTableSetup.FromBulkCopy(FinishedBulkCopy(r => r.TargetTablePreExisted = true), 1, 2, "hr", DateTimeOffset.UnixEpoch);
        Assert.Equal(TrackedTableStatus.Ready, tracked!.Status);
    }

    [Fact]
    public void The_frozen_columns_are_the_copied_ones_in_copy_order()
    {
        var (tracked, _) = TrackedTableSetup.FromBulkCopy(FinishedBulkCopy(), 1, 2, "hr", DateTimeOffset.UnixEpoch);
        var columns = JsonSerializer.Deserialize<List<TrackedColumn>>(tracked!.ColumnsJson, TrackedTableSetup.Json)!;

        // Ordered by id - the positional contract between reader and writer - and without the excluded one.
        Assert.Equal(new[] { "ID", "NAME" }, columns.Select(c => c.Name));
        Assert.Equal("bigint", columns[0].PostgresType);
    }

    [Theory]
    [InlineData("scn", "position in its change history")]
    [InlineData("logging", "change logging was not fully on")]
    [InlineData("key", "no usable primary key")]
    [InlineData("objects", "object ids could not be read")]
    public void A_copy_missing_something_it_needs_is_not_tracked_and_says_why(string missing, string expected)
    {
        var run = FinishedBulkCopy(r =>
        {
            if (missing == "scn") r.SourceStartScn = null;
            if (missing == "logging") r.LoggingReadyAtStart = false;
            if (missing == "key") r.SourceKeyJson = null;
            if (missing == "objects") r.SourceObjectIdsJson = null;
        });

        var (tracked, reason) = TrackedTableSetup.FromBulkCopy(run, 1, 2, "hr", DateTimeOffset.UnixEpoch);

        Assert.Null(tracked);
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void A_key_column_left_out_of_the_selection_prevents_tracking()
    {
        var run = FinishedBulkCopy(r => r.ManifestTable.Columns.Single(c => c.ColumnName == "ID").IsExcluded = true);

        var (tracked, reason) = TrackedTableSetup.FromBulkCopy(run, 1, 2, "hr", DateTimeOffset.UnixEpoch);

        Assert.Null(tracked);
        Assert.Contains("ID", reason);
    }

    [Fact]
    public void Refreshing_an_existing_tracker_keeps_its_identity_and_clears_any_claim()
    {
        var existing = new TrackedTable { Id = 9, CreatedAt = DateTimeOffset.UnixEpoch, ActiveJobRunId = 4, Status = TrackedTableStatus.NeedsBulkCopy, LastError = "old" };
        var (fresh, _) = TrackedTableSetup.FromBulkCopy(FinishedBulkCopy(), 1, 2, "hr", DateTimeOffset.UnixEpoch.AddDays(1));

        TrackedTableSetup.Apply(existing, fresh!);

        Assert.Equal(9, existing.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch, existing.CreatedAt);
        Assert.Null(existing.ActiveJobRunId);
        Assert.Null(existing.LastError);
        Assert.Equal(TrackedTableStatus.NeedsFirstSync, existing.Status);
    }

    [Fact]
    public void Key_json_written_by_the_engine_reads_back_whatever_its_casing() =>
        // Written in one place and read in another: a casing mismatch would not fail, it would read empty keys.
        Assert.Equal("ID", JsonSerializer.Deserialize<List<OracleKeyColumn>>("[{\"Name\":\"ID\",\"DataType\":\"NUMBER\"}]", TrackedTableSetup.Json)!.Single().Name);
}
