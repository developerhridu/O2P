using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace O2P.Application.Schema
{
    public static class SqlIdentifier
    {
        // Quoted Oracle identifiers may start with underscore (e.g. "_DATE"). Unquoted names
        // must start with a letter; we always emit quoted form, so allow leading '_'.
        private static readonly Regex OracleIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_$#]{0,127}$", RegexOptions.Compiled);

        public static string QuotePostgres(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("Identifier cannot be blank.", nameof(identifier));
            }

            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        public static string QuotePostgresQualified(params string[] identifiers)
        {
            return string.Join(".", identifiers.Select(QuotePostgres));
        }

        public static string QuoteOracle(string identifier)
        {
            if (!OracleIdentifier.IsMatch(identifier))
            {
                throw new ArgumentException($"Unsafe Oracle identifier: {identifier}", nameof(identifier));
            }

            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        public static string OracleQualified(string owner, string table)
        {
            return $"{QuoteOracle(owner)}.{QuoteOracle(table)}";
        }

        public static IReadOnlyList<string> IncludedColumnNames(O2P.Domain.Entities.ManifestTable table)
        {
            return table.Columns
                .Where(c => !c.IsExcluded)
                .OrderBy(c => c.Id)
                .Select(c => c.ColumnName)
                .ToList();
        }
    }
}
