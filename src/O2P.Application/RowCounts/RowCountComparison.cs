using System;
using System.Collections.Generic;
using System.Linq;

namespace O2P.Application.RowCounts
{
    /// <summary>One table's last count on one side.</summary>
    public sealed record TableCount(string Table, long? Rows, DateTimeOffset? CountedAt, long? DurationMs, string? Error);

    /// <summary>A source table and the destination table it pairs with; either may be missing.</summary>
    public sealed record TablePair(string Key, TableCount? Source, TableCount? Destination, long? Difference, string Status);

    public sealed record ComparisonSummary(
        int Tables,
        int Matching,
        int Different,
        int OnlySource,
        int OnlyDestination,
        int NotCounted,
        int Errors,
        long SourceRows,
        long DestinationRows);

    public static class PairStatus
    {
        public const string Match = "match";
        /// <summary>The destination has fewer rows than the source.</summary>
        public const string MissingRows = "missing_rows";
        /// <summary>The destination has more rows than the source.</summary>
        public const string ExtraRows = "extra_rows";
        public const string OnlySource = "only_source";
        public const string OnlyDestination = "only_destination";
        public const string NotCounted = "not_counted";
        public const string Error = "error";
    }

    /// <summary>
    /// Pairs the tables of a source schema with those of a destination schema by name and compares their
    /// row counts, for the Dashboard.
    ///
    /// Names are matched ignoring case, since O2P writes Oracle's ORDERS as PostgreSQL's orders. An exact
    /// match is preferred, so when a destination holds both ORDERS (an older upper-case copy) and orders,
    /// ORDERS pairs with ORDERS and orders is shown on its own. Each table is used at most once.
    /// </summary>
    public static class RowCountComparison
    {
        public static IReadOnlyList<TablePair> Pair(IEnumerable<TableCount> source, IEnumerable<TableCount> destination)
        {
            var sources = source.OrderBy(t => t.Table, StringComparer.Ordinal).ToList();
            var unused = destination.OrderBy(t => t.Table, StringComparer.Ordinal).ToList();
            var matched = new Dictionary<TableCount, TableCount>();

            // Exact names first, so a case-insensitive match can never take a table that has an exact partner.
            foreach (var s in sources)
            {
                var exact = unused.FirstOrDefault(d => string.Equals(d.Table, s.Table, StringComparison.Ordinal));
                if (exact == null) continue;
                matched[s] = exact;
                unused.Remove(exact);
            }
            foreach (var s in sources.Where(s => !matched.ContainsKey(s)))
            {
                var loose = unused.FirstOrDefault(d => string.Equals(d.Table, s.Table, StringComparison.OrdinalIgnoreCase));
                if (loose == null) continue;
                matched[s] = loose;
                unused.Remove(loose);
            }

            var pairs = sources
                .Select(s => Compare(s.Table, s, matched.TryGetValue(s, out var d) ? d : null))
                .Concat(unused.Select(d => Compare(d.Table, null, d)));

            return pairs
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .ToList();
        }

        public static TablePair Compare(string key, TableCount? source, TableCount? destination)
        {
            if (destination == null) return new TablePair(key, source, null, null, PairStatus.OnlySource);
            if (source == null) return new TablePair(key, null, destination, null, PairStatus.OnlyDestination);

            // A failed count means the number shown (if any) is from before; do not call it a match or not.
            if (source.Error != null || destination.Error != null)
                return new TablePair(key, source, destination, null, PairStatus.Error);

            if (source.Rows is not long s || destination.Rows is not long d)
                return new TablePair(key, source, destination, null, PairStatus.NotCounted);

            var difference = d - s;
            var status = difference == 0 ? PairStatus.Match
                : difference < 0 ? PairStatus.MissingRows
                : PairStatus.ExtraRows;
            return new TablePair(key, source, destination, difference, status);
        }

        public static ComparisonSummary Summarise(IReadOnlyCollection<TablePair> pairs) => new(
            Tables: pairs.Count,
            Matching: pairs.Count(p => p.Status == PairStatus.Match),
            Different: pairs.Count(p => p.Status is PairStatus.MissingRows or PairStatus.ExtraRows),
            OnlySource: pairs.Count(p => p.Status == PairStatus.OnlySource),
            OnlyDestination: pairs.Count(p => p.Status == PairStatus.OnlyDestination),
            NotCounted: pairs.Count(p => p.Status == PairStatus.NotCounted),
            Errors: pairs.Count(p => p.Status == PairStatus.Error),
            SourceRows: pairs.Sum(p => p.Source?.Rows ?? 0),
            DestinationRows: pairs.Sum(p => p.Destination?.Rows ?? 0));
    }
}
