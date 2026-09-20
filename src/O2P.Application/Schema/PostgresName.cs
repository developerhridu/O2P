using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace O2P.Application.Schema
{
    /// <summary>
    /// Every name O2P creates in the destination goes through here.
    ///
    /// Oracle hands over UPPERCASE names and SqlIdentifier always double-quotes, so a copied table used
    /// to land as "BL_NOTIFICATION"."LAST_UPDATED" - and a quoted uppercase name stays uppercase, which
    /// means every query against it has to be quoted for ever. Postgres folds unquoted identifiers to
    /// lower case, so lower-case names are the only ones that can be typed without quotes.
    ///
    /// Tables created by earlier runs are still out there under their original spelling, so the spelling
    /// is not a global switch: it is decided per table run (see MigrationEngine) and carried on
    /// TableRun.TargetNameStyle, which is why every lookup here takes a style.
    /// </summary>
    public static class PostgresName
    {
        /// <summary>Names O2P creates from now on: lower case.</summary>
        public const string LowerStyle = "lower";

        /// <summary>A table an earlier run created: keep whatever Oracle called it.</summary>
        public const string SourceStyle = "source";

        /// <summary>
        /// ToLowerInvariant, not ToLower: under a Turkish locale "I".ToLower() is "ı", which would
        /// produce a name nobody can type and which no other machine would agree on.
        /// </summary>
        public static string For(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Identifier cannot be blank.", nameof(name));
            }

            return name.ToLowerInvariant();
        }

        /// <summary>
        /// Null means a run recorded before the style was tracked, which can only be a table from back
        /// then - so it reads as "source" and nothing already in the database changes meaning.
        /// </summary>
        public static bool IsLower(string? style) => string.Equals(style, LowerStyle, StringComparison.Ordinal);

        /// <summary>The destination name of one column under the run's style.</summary>
        public static string TargetColumn(ManifestColumn column, string? style) =>
            IsLower(style) ? For(column.ColumnName) : column.ColumnName;

        /// <summary>The destination names of a table's loaded columns, in COPY order.</summary>
        public static IReadOnlyList<string> TargetColumns(IEnumerable<ManifestColumn> columns, string? style) =>
            columns.Select(c => TargetColumn(c, style)).ToList();

        /// <summary>
        /// Names that land on top of each other once lower-cased. Oracle can hold "Emp" and "EMP" as two
        /// tables, and "Col" and "COL" as two columns of one table; left unchecked the first shows up as
        /// a unique-index violation on table_runs and the second as COPY's "column specified more than
        /// once", both mid-run and neither explicable. Each group is returned in its original spelling
        /// so the message can name what actually clashes.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<string>> FindCollisions(IEnumerable<string> names)
        {
            return names
                .Distinct(StringComparer.Ordinal)
                .GroupBy(For, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => (IReadOnlyList<string>)g.OrderBy(n => n, StringComparer.Ordinal).ToList())
                .ToList();
        }

        /// <summary>Renders <see cref="FindCollisions"/> for a message the operator can act on.</summary>
        public static string DescribeCollisions(IReadOnlyList<IReadOnlyList<string>> collisions) =>
            string.Join("; ", collisions.Select(g => $"{string.Join(" and ", g.Select(n => $"\"{n}\""))} would both become \"{For(g[0])}\""));
    }
}
