using System;
using System.Linq;
using O2P.Application.RowCounts;
using Xunit;

namespace O2P.Application.Tests;

public class RowCountComparisonTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    private static TableCount T(string name, long? rows = 10, string? error = null) =>
        new(name, rows, rows.HasValue ? At : null, 5, error);

    [Fact]
    public void Pairs_upper_case_source_with_lower_case_destination()
    {
        var pairs = RowCountComparison.Pair(new[] { T("ORDERS") }, new[] { T("orders") });

        var pair = Assert.Single(pairs);
        Assert.Equal("ORDERS", pair.Source!.Table);
        Assert.Equal("orders", pair.Destination!.Table);
        Assert.Equal(PairStatus.Match, pair.Status);
        Assert.Equal(0, pair.Difference);
    }

    [Fact]
    public void An_exact_name_wins_over_a_case_insensitive_one()
    {
        // An older upper-case copy and a newer lower-case one both exist in the destination.
        var pairs = RowCountComparison.Pair(new[] { T("ORDERS") }, new[] { T("orders"), T("ORDERS") });

        Assert.Equal(2, pairs.Count);
        var paired = pairs.Single(p => p.Source != null);
        Assert.Equal("ORDERS", paired.Destination!.Table);
        var alone = pairs.Single(p => p.Source == null);
        Assert.Equal("orders", alone.Destination!.Table);
        Assert.Equal(PairStatus.OnlyDestination, alone.Status);
    }

    [Fact]
    public void An_exact_partner_is_never_taken_by_another_tables_loose_match()
    {
        // Oracle can hold both "Orders" (quoted) and ORDERS; each should find its own partner.
        var pairs = RowCountComparison.Pair(new[] { T("Orders"), T("ORDERS") }, new[] { T("ORDERS"), T("orders") });

        Assert.Equal(2, pairs.Count);
        Assert.Equal("ORDERS", pairs.Single(p => p.Source!.Table == "ORDERS").Destination!.Table);
        Assert.Equal("orders", pairs.Single(p => p.Source!.Table == "Orders").Destination!.Table);
    }

    [Fact]
    public void Tables_on_one_side_only_are_listed_with_that_side()
    {
        var pairs = RowCountComparison.Pair(new[] { T("AUDIT_LOG"), T("ORDERS") }, new[] { T("orders"), T("tmp_import") });

        Assert.Equal(PairStatus.OnlySource, pairs.Single(p => p.Key == "AUDIT_LOG").Status);
        Assert.Equal(PairStatus.OnlyDestination, pairs.Single(p => p.Key == "tmp_import").Status);
        Assert.Null(pairs.Single(p => p.Key == "AUDIT_LOG").Difference);
    }

    [Theory]
    [InlineData(100L, 100L, PairStatus.Match, 0L)]
    [InlineData(100L, 88L, PairStatus.MissingRows, -12L)]
    [InlineData(100L, 101L, PairStatus.ExtraRows, 1L)]
    [InlineData(0L, 0L, PairStatus.Match, 0L)]
    public void Compares_counts(long source, long destination, string status, long difference)
    {
        var pair = RowCountComparison.Compare("T", T("T", source), T("t", destination));
        Assert.Equal(status, pair.Status);
        Assert.Equal(difference, pair.Difference);
    }

    [Fact]
    public void Uncounted_on_either_side_is_not_counted()
    {
        Assert.Equal(PairStatus.NotCounted, RowCountComparison.Compare("T", T("T", null), T("t", 5)).Status);
        Assert.Equal(PairStatus.NotCounted, RowCountComparison.Compare("T", T("T", 5), T("t", null)).Status);
    }

    [Fact]
    public void A_failed_count_is_an_error_even_with_an_older_number()
    {
        var pair = RowCountComparison.Compare("T", T("T", 5, error: "ORA-00942"), T("t", 5));
        Assert.Equal(PairStatus.Error, pair.Status);
        Assert.Null(pair.Difference);
    }

    [Fact]
    public void Sorted_by_name_ignoring_case()
    {
        var pairs = RowCountComparison.Pair(new[] { T("ZEBRA"), T("APPLE") }, new[] { T("mango") });
        Assert.Equal(new[] { "APPLE", "mango", "ZEBRA" }, pairs.Select(p => p.Key));
    }

    [Fact]
    public void Summary_adds_up()
    {
        var pairs = RowCountComparison.Pair(
            new[] { T("A", 10), T("B", 10), T("C", 10), T("D", null), T("E", 3, "boom") },
            new[] { T("a", 10), T("b", 7), T("d", 4), T("e", 3), T("x", 1) });

        var s = RowCountComparison.Summarise(pairs);
        Assert.Equal(6, s.Tables);
        Assert.Equal(1, s.Matching);
        Assert.Equal(1, s.Different);
        Assert.Equal(1, s.OnlySource);
        Assert.Equal(1, s.OnlyDestination);
        Assert.Equal(1, s.NotCounted);
        Assert.Equal(1, s.Errors);
        Assert.Equal(33, s.SourceRows);
        Assert.Equal(25, s.DestinationRows);
    }
}
