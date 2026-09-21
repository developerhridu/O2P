using O2P.Application.Interfaces;
using O2P.Application.Schema;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    /// <summary>
    /// Keys travel as text end to end: that is what LogMiner hands out, and Oracle NUMBER(38) does not
    /// fit a .NET decimal. This turns that text back into a bind of the exact Oracle type - never a
    /// TO_NUMBER/TO_DATE against the session's NLS settings, which the re-read must leave as the bulk
    /// copy had them, since an operator's row filter may lean on them.
    /// </summary>
    internal static class OracleKeyBinding
    {
        /// <summary>
        /// Batches are padded to this many keys by repeating the last one, so every batch is the same SQL
        /// text and Oracle reuses one cursor instead of hard-parsing each batch.
        /// </summary>
        public const int BatchSize = 250;

        /// <summary>The fixed text formats the mining session is set to, and that keys are parsed from.</summary>
        public const string DateFormatOracle = "YYYY-MM-DD HH24:MI:SS";
        public const string DateFormatNet = "yyyy-MM-dd HH:mm:ss";

        public static OracleParameter Bind(OracleKeyColumn column, string? text, string name)
        {
            if (text == null) return new OracleParameter(name, DBNull.Value);

            switch (column.DataType.ToUpperInvariant())
            {
                case "NUMBER":
                case "INTEGER":
                case "FLOAT":
                    // OracleDecimal holds all 38 digits; decimal would round the tail off.
                    return new OracleParameter(name, OracleDbType.Decimal) { Value = OracleDecimal.Parse(text) };

                case "DATE":
                    return new OracleParameter(name, OracleDbType.Date)
                    {
                        Value = DateTime.ParseExact(text, DateFormatNet, CultureInfo.InvariantCulture)
                    };

                case "RAW":
                    return new OracleParameter(name, OracleDbType.Raw) { Value = Convert.FromHexString(text) };

                case "CHAR":
                    return new OracleParameter(name, OracleDbType.Char) { Value = text };

                case "NCHAR":
                    return new OracleParameter(name, OracleDbType.NChar) { Value = text };

                case "NVARCHAR2":
                    return new OracleParameter(name, OracleDbType.NVarchar2) { Value = text };

                default:
                    return new OracleParameter(name, OracleDbType.Varchar2) { Value = text };
            }
        }

        /// <summary>
        /// "(k) IN ((:p0), ...)" for one batch - fixed size, padded with the last key - and its binds.
        /// Row-value form throughout, so a composite key needs no special case.
        /// </summary>
        public static (string Sql, List<OracleParameter> Parameters) KeyPredicate(
            IReadOnlyList<OracleKeyColumn> key, IReadOnlyList<IReadOnlyList<string?>> batch)
        {
            if (batch.Count == 0 || batch.Count > BatchSize)
            {
                throw new ArgumentOutOfRangeException(nameof(batch), $"A batch holds 1 to {BatchSize} keys.");
            }

            var columns = "(" + string.Join(", ", key.Select(k => SqlIdentifier.QuoteOracle(k.Name))) + ")";
            var parameters = new List<OracleParameter>(BatchSize * key.Count);
            var tuples = new StringBuilder();

            for (var i = 0; i < BatchSize; i++)
            {
                var values = batch[Math.Min(i, batch.Count - 1)];
                if (i > 0) tuples.Append(", ");
                tuples.Append('(');
                for (var c = 0; c < key.Count; c++)
                {
                    var name = $"k{i}_{c}";
                    if (c > 0) tuples.Append(", ");
                    tuples.Append(':').Append(name);
                    parameters.Add(Bind(key[c], values[c], name));
                }
                tuples.Append(')');
            }

            return ($"{columns} IN ({tuples})", parameters);
        }

        /// <summary>
        /// A key column read back from a table (for a LOB change, which logs no key) rendered the way the
        /// mining session renders it, so both routes produce the same text for the same key.
        /// </summary>
        public static string SelectAsText(OracleKeyColumn column)
        {
            var quoted = SqlIdentifier.QuoteOracle(column.Name);
            return column.DataType.ToUpperInvariant() switch
            {
                "NUMBER" or "INTEGER" or "FLOAT" => $"TO_CHAR({quoted}, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')",
                "DATE" => $"TO_CHAR({quoted}, '{DateFormatOracle}')",
                "RAW" => $"RAWTOHEX({quoted})",
                _ => quoted
            };
        }
    }
}
