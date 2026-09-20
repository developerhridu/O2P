using O2P.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace O2P.Application.Schema
{
    public enum MismatchSeverity
    {
        /// <summary>The load would abort, or would silently store wrong data. Blocks the table.</summary>
        Fail,

        /// <summary>The load would succeed, but something is worth knowing. Does not block.</summary>
        Warn
    }

    public sealed record SchemaMismatch(string ColumnName, MismatchSeverity Severity, string Message);

    /// <summary>
    /// Decides whether a pre-existing target table can accept a manifest's columns, without touching
    /// a database. Pure so it can be tested directly.
    ///
    /// The whole classification rests on one fact: a COPY BINARY stream carries no type OIDs. Each
    /// field is a length plus raw bytes, and Postgres calls the *target column's* receive function on
    /// them, while Npgsql picks those bytes from the manifest type (PostgresBinaryWriter). So widening
    /// is NOT safe - manifest "integer" into a target "bigint" sends 4 bytes to int8recv and aborts on
    /// the first row. A few mismatches are worse than an error: timestamp into timestamptz and text
    /// into bytea are both accepted silently and store wrong data.
    /// </summary>
    public static class TargetSchemaComparer
    {
        public static IReadOnlyList<SchemaMismatch> Compare(ManifestTable manifest, IReadOnlyList<PostgresLiveColumn> liveColumns)
        {
            var problems = new List<SchemaMismatch>();

            // Exactly the projection the COPY column list uses (PostgresBinaryWriter, Worker):
            // only these columns are ever written, so only these have to line up.
            var manifestColumns = manifest.Columns
                .Where(c => !c.IsExcluded)
                .OrderBy(c => c.Id)
                .ToList();

            var liveByName = liveColumns
                .GroupBy(c => c.ColumnName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Ordinal, not case-insensitive: SqlIdentifier.QuotePostgres always double-quotes, so
            // "EMP_ID" binds only to a column physically named EMP_ID.
            var accountedFor = new HashSet<string>(StringComparer.Ordinal);

            foreach (var column in manifestColumns)
            {
                if (!liveByName.TryGetValue(column.ColumnName, out var live))
                {
                    problems.Add(new SchemaMismatch(
                        column.ColumnName,
                        MismatchSeverity.Fail,
                        DescribeMissing(column.ColumnName, liveColumns, out var nearMiss)));

                    // Claim the near-miss so it is not also reported as a stray extra column.
                    if (nearMiss != null) accountedFor.Add(nearMiss.ColumnName);
                    continue;
                }

                accountedFor.Add(live.ColumnName);

                var typeProblem = CompareTypes(column, live);
                if (typeProblem != null) problems.Add(typeProblem);

                if (live.NotNull && column.IsNullable)
                {
                    // Data-dependent, not structural: "Oracle permits nulls" is not "contains nulls",
                    // and failing here would block plenty of tables that load perfectly well.
                    problems.Add(new SchemaMismatch(
                        column.ColumnName,
                        MismatchSeverity.Warn,
                        $"column \"{column.ColumnName}\": the destination requires a value but the source allows empty ones; a null row would abort the load"));
                }
            }

            foreach (var live in liveColumns)
            {
                if (accountedFor.Contains(live.ColumnName)) continue;
                if (!live.NotNull || live.IsSelfPopulating) continue;

                problems.Add(new SchemaMismatch(
                    live.ColumnName,
                    MismatchSeverity.Fail,
                    $"column \"{live.ColumnName}\": extra required (NOT NULL) column in the destination with no default, identity or generation expression; the loader does not populate it"));
            }

            return problems;
        }

        public static bool HasBlockingProblem(IReadOnlyList<SchemaMismatch> problems) =>
            problems.Any(p => p.Severity == MismatchSeverity.Fail);

        private static string DescribeMissing(
            string columnName,
            IReadOnlyList<PostgresLiveColumn> liveColumns,
            out PostgresLiveColumn? nearMiss)
        {
            // Oracle discovery yields UPPERCASE names while hand-built Postgres tables are
            // conventionally lowercase, so this is the mismatch operators will hit most. Name the
            // real spelling instead of just saying "missing" - but never fold automatically, which
            // would silently redirect the load.
            var candidates = liveColumns
                .Where(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1)
            {
                nearMiss = candidates[0];
                return $"column \"{columnName}\": not found in the destination table; the destination has \"{candidates[0].ColumnName}\" - names are case-sensitive because the loader quotes every identifier";
            }

            nearMiss = null;
            return $"column \"{columnName}\": not found in the destination table";
        }

        private static SchemaMismatch? CompareTypes(ManifestColumn column, PostgresLiveColumn live)
        {
            var source = PostgresType.Parse(column.PostgresDataType);
            var target = PostgresType.Parse(live.FormattedType);

            var severity = MismatchSeverity.Fail;
            var reason = Classify(source, target, ref severity);
            if (reason == null) return null;

            return new SchemaMismatch(
                column.ColumnName,
                severity,
                $"column \"{column.ColumnName}\": the selected table needs {column.PostgresDataType}, the destination is {live.FormattedType} - {reason}");
        }

        private static string? Classify(PostgresType source, PostgresType target, ref MismatchSeverity severity)
        {
            if (source.Family == TypeFamily.Unknown || target.Family == TypeFamily.Unknown)
            {
                // A domain, enum or array. A domain over varchar would in fact load, but guessing is
                // worse than stopping with a message the operator can act on.
                return "unrecognised type, so O2P cannot be sure the data would load correctly";
            }

            switch (source.Family)
            {
                case TypeFamily.String:
                    return ClassifyString(source.Arg1, target, ref severity);

                case TypeFamily.Xml:
                    if (target.Family == TypeFamily.Xml) return null;
                    // Npgsql writes XML as a CLR string, so a text target takes it unchanged.
                    if (target.Family == TypeFamily.String) return ClassifyString(null, target, ref severity);
                    return "incompatible binary COPY format";

                case TypeFamily.Integer:
                case TypeFamily.Float:
                    // Fixed width, byte-exact. Widening is just as broken as narrowing.
                    return target.Family == source.Family && target.Canonical == source.Canonical
                        ? null
                        : "incompatible binary COPY format; fixed-width types must match exactly, widening included";

                case TypeFamily.Numeric:
                    return ClassifyNumeric(source, target);

                case TypeFamily.Timestamp:
                    return ClassifyTimestamp(source, target, ref severity);

                default:
                    return target.Family == source.Family && target.Canonical == source.Canonical
                        ? null
                        : "incompatible binary COPY format";
            }
        }

        private static string? ClassifyString(int? sourceMaxLength, PostgresType target, ref MismatchSeverity severity)
        {
            if (target.Family == TypeFamily.Xml)
            {
                return "the destination is xml; the XML parser rejects any value that is not well-formed";
            }

            if (target.Family == TypeFamily.Bytea)
            {
                // byteareceive takes any bytes, so this is accepted silently.
                return "the destination is bytea; COPY would accept the text as raw bytes and store it corrupted without an error";
            }

            if (target.Family != TypeFamily.String)
            {
                return "incompatible binary COPY format";
            }

            if (target.Arg1 != null)
            {
                if (sourceMaxLength == null)
                {
                    return $"the destination is limited to {target.Arg1} characters but the source is unbounded; a longer row would abort the load";
                }

                if (target.Arg1 < sourceMaxLength)
                {
                    return $"the destination is narrower than the source; rows longer than {target.Arg1} characters would abort the load";
                }
            }

            if (target.IsBlankPadded)
            {
                severity = MismatchSeverity.Warn;
                return "the destination is a blank-padded character(n); every value is padded out to the full width";
            }

            return null;
        }

        private static string? ClassifyNumeric(PostgresType source, PostgresType target)
        {
            if (target.Family != TypeFamily.Numeric)
            {
                return "incompatible binary COPY format";
            }

            // An unconstrained numeric target takes anything the source can produce.
            if (target.Arg1 == null) return null;

            if (source.Arg1 == null)
            {
                return $"the destination is constrained to numeric({target.Arg1},{target.Arg2 ?? 0}) but the source is unconstrained";
            }

            // TypeMapper emits "numeric(18)" where format_type reports "numeric(18,0)" - same thing.
            var sourceScale = source.Arg2 ?? 0;
            var targetScale = target.Arg2 ?? 0;

            if (targetScale < sourceScale)
            {
                return $"the destination scale {targetScale} is smaller than the source scale {sourceScale}; Postgres would round every value without raising an error";
            }

            var sourceWhole = source.Arg1.Value - sourceScale;
            var targetWhole = target.Arg1.Value - targetScale;
            if (targetWhole < sourceWhole)
            {
                return $"the destination holds {targetWhole} digits before the decimal point but the source needs {sourceWhole}; a large value would abort the load";
            }

            return null;
        }

        private static string? ClassifyTimestamp(PostgresType source, PostgresType target, ref MismatchSeverity severity)
        {
            if (target.Family == TypeFamily.Date)
            {
                // Oracle DATE maps to timestamp(0) while hand-built Postgres targets usually use date.
                return "the destination is date (4 bytes) but a timestamp is sent as 8 bytes; the load would abort on the first row";
            }

            if (target.Family != TypeFamily.Timestamp)
            {
                return "incompatible binary COPY format";
            }

            if (source.WithTimeZone != target.WithTimeZone)
            {
                // Both are int8 microseconds, so COPY accepts this and quietly reinterprets it.
                return target.WithTimeZone
                    ? "the destination is 'with time zone'; COPY accepts the bytes but reinterprets every value as UTC, shifting it with no error"
                    : "the destination is 'without time zone'; COPY accepts the bytes but drops the zone, shifting every value with no error";
            }

            // No parens means typmod -1, which is precision 6.
            var sourcePrecision = source.Arg1 ?? 6;
            var targetPrecision = target.Arg1 ?? 6;
            if (targetPrecision < sourcePrecision)
            {
                severity = MismatchSeverity.Warn;
                return $"the destination keeps {targetPrecision} fractional digits where the source has {sourcePrecision}; Postgres rounds the difference away";
            }

            return null;
        }

        private enum TypeFamily
        {
            Unknown,
            String,
            Integer,
            Float,
            Numeric,
            Timestamp,
            Date,
            Time,
            Interval,
            Bytea,
            Xml,
            Boolean,
            Uuid,
            Json
        }

        /// <summary>
        /// A type string reduced to something comparable, whether it came from TypeMapper
        /// (e.g. "numeric(18, 2)", "varchar(255)") or from format_type (e.g. "numeric(18,2)",
        /// "character varying(255)").
        /// </summary>
        private sealed record PostgresType(
            TypeFamily Family,
            string Canonical,
            int? Arg1,
            int? Arg2,
            bool WithTimeZone,
            bool IsBlankPadded)
        {
            private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

            public static PostgresType Parse(string? rawType)
            {
                var text = WhitespaceRun.Replace((rawType ?? string.Empty).Trim().ToLowerInvariant(), " ");

                int? arg1 = null;
                int? arg2 = null;

                // The modifier can sit in the middle: "timestamp(0) without time zone". Pull it out
                // and rejoin what is left, which also collapses TypeMapper's "numeric(18, 2)" and
                // format_type's "numeric(18,2)" onto the same base name.
                var open = text.IndexOf('(');
                if (open >= 0)
                {
                    var close = text.IndexOf(')', open + 1);
                    if (close > open)
                    {
                        var args = text.Substring(open + 1, close - open - 1).Split(',');
                        if (args.Length > 0 && TryParseInt(args[0], out var parsed1)) arg1 = parsed1;
                        if (args.Length > 1 && TryParseInt(args[1], out var parsed2)) arg2 = parsed2;

                        text = WhitespaceRun
                            .Replace(text.Substring(0, open) + " " + text.Substring(close + 1), " ")
                            .Trim();
                    }
                }

                var canonical = Fold(text);

                return canonical switch
                {
                    "text" => new PostgresType(TypeFamily.String, canonical, null, null, false, false),
                    "character varying" => new PostgresType(TypeFamily.String, canonical, arg1, null, false, false),
                    "character" => new PostgresType(TypeFamily.String, canonical, arg1, null, false, true),
                    "smallint" or "integer" or "bigint" => new PostgresType(TypeFamily.Integer, canonical, null, null, false, false),
                    "real" or "double precision" => new PostgresType(TypeFamily.Float, canonical, null, null, false, false),
                    "numeric" => new PostgresType(TypeFamily.Numeric, canonical, arg1, arg2, false, false),
                    "timestamp without time zone" => new PostgresType(TypeFamily.Timestamp, canonical, arg1, null, false, false),
                    "timestamp with time zone" => new PostgresType(TypeFamily.Timestamp, canonical, arg1, null, true, false),
                    "date" => new PostgresType(TypeFamily.Date, canonical, null, null, false, false),
                    "time without time zone" => new PostgresType(TypeFamily.Time, canonical, arg1, null, false, false),
                    "time with time zone" => new PostgresType(TypeFamily.Time, canonical, arg1, null, true, false),
                    "interval" => new PostgresType(TypeFamily.Interval, canonical, arg1, null, false, false),
                    "bytea" => new PostgresType(TypeFamily.Bytea, canonical, null, null, false, false),
                    "xml" => new PostgresType(TypeFamily.Xml, canonical, null, null, false, false),
                    "boolean" => new PostgresType(TypeFamily.Boolean, canonical, null, null, false, false),
                    "uuid" => new PostgresType(TypeFamily.Uuid, canonical, null, null, false, false),
                    "json" or "jsonb" => new PostgresType(TypeFamily.Json, canonical, null, null, false, false),
                    _ => new PostgresType(TypeFamily.Unknown, canonical, arg1, arg2, false, false)
                };
            }

            private static bool TryParseInt(string text, out int value) =>
                int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

            // format_type never emits an alias, so this is really for the manifest side - but keeping
            // it symmetric costs nothing and covers a hand-edited PostgresDataType.
            private static string Fold(string baseName) => baseName switch
            {
                "varchar" => "character varying",
                "char" or "bpchar" => "character",
                "int2" => "smallint",
                "int" or "int4" => "integer",
                "int8" => "bigint",
                "float4" => "real",
                "float8" => "double precision",
                "decimal" => "numeric",
                "bool" => "boolean",
                "timestamptz" => "timestamp with time zone",
                "timestamp" => "timestamp without time zone",
                "timetz" => "time with time zone",
                "time" => "time without time zone",
                _ => baseName
            };
        }
    }
}
