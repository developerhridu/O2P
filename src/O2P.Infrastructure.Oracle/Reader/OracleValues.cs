using System;
using System.Collections.Generic;
using System.Linq;

namespace O2P.Infrastructure.Oracle.Reader
{
    /// <summary>
    /// How a value read from Oracle becomes what O2P writes, and which columns need LOB-aware fetching.
    /// Shared by the bulk reader and the change-tracking re-read on purpose: a change copy must write
    /// exactly what a bulk copy would have written for the same row, or a row it only re-reads would
    /// quietly differ from one the bulk copy loaded.
    /// </summary>
    internal static class OracleValues
    {
        private static readonly HashSet<string> LobOracleTypes =
            new(StringComparer.OrdinalIgnoreCase) { "BLOB", "CLOB", "NCLOB", "LONG", "LONG RAW", "BFILE" };

        /// <summary>
        /// True for Oracle LOB types (as opposed to bounded RAW/VARCHAR2), which need LOB-aware fetch
        /// tuning to avoid stalling the read.
        /// </summary>
        public static bool IsLob(string? oracleType)
        {
            if (string.IsNullOrWhiteSpace(oracleType)) return false;
            var baseType = oracleType.Trim().Split('(')[0].Trim().ToUpperInvariant();
            return LobOracleTypes.Contains(baseType);
        }

        public static object Normalize(object value)
        {
            if (value == DBNull.Value)
            {
                return DBNull.Value;
            }

            if (value is string text)
            {
                var sanitized = text.Replace("\0", string.Empty);
                return sanitized.Length == 0 ? DBNull.Value : sanitized;
            }

            return value;
        }

        /// <summary>
        /// The row filter is free text typed by an operator. Only a read-only predicate is allowed; this is
        /// a denylist, not a parser, so it is deliberately blunt.
        /// </summary>
        public static string ValidateReadOnlyWhereClause(string whereClause)
        {
            var forbidden = new[] { ";", "--", "/*", "*/", " insert ", " update ", " delete ", " merge ", " drop ", " alter ", " create ", " execute ", " grant ", " revoke " };
            var normalized = " " + whereClause.ToLowerInvariant() + " ";
            if (forbidden.Any(normalized.Contains))
            {
                throw new ArgumentException("The manifest WHERE clause contains unsupported SQL.");
            }

            return whereClause;
        }
    }
}
