using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Postgres.Writer
{
    /// <summary>
    /// How each value goes into a binary COPY. Shared by the bulk writer and the change applier, so a row
    /// a change copy writes is byte-for-byte what a bulk copy would have written for the same row.
    ///
    /// Oracle NUMBER/FLOAT arrive from ODP.NET as .NET decimal whatever the destination column type, and
    /// a decimal written by type inference produces numeric binary format, which Postgres rejects against
    /// an integer/real/double column ("22P03: incorrect binary data format"). So those columns are written
    /// with an explicit NpgsqlDbType and the value coerced to match.
    /// </summary>
    internal readonly record struct PostgresWritePlan(bool Typed, NpgsqlDbType DbType, Func<object, object> Coerce)
    {
        public static PostgresWritePlan Resolve(string postgresType)
        {
            var baseType = Regex.Replace((postgresType ?? string.Empty).Trim().ToLowerInvariant(), @"\(.*?\)", "").Trim();
            return baseType switch
            {
                "smallint" => new PostgresWritePlan(true, NpgsqlDbType.Smallint, v => Convert.ToInt16(v, CultureInfo.InvariantCulture)),
                "integer" => new PostgresWritePlan(true, NpgsqlDbType.Integer, v => Convert.ToInt32(v, CultureInfo.InvariantCulture)),
                "bigint" => new PostgresWritePlan(true, NpgsqlDbType.Bigint, v => Convert.ToInt64(v, CultureInfo.InvariantCulture)),
                "real" => new PostgresWritePlan(true, NpgsqlDbType.Real, v => Convert.ToSingle(v, CultureInfo.InvariantCulture)),
                "double precision" => new PostgresWritePlan(true, NpgsqlDbType.Double, v => Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                "numeric" => new PostgresWritePlan(true, NpgsqlDbType.Numeric, v => Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
                _ => new PostgresWritePlan(false, default, v => v),
            };
        }

        public static PostgresWritePlan[] ResolveAll(IEnumerable<string> postgresTypes) =>
            postgresTypes.Select(Resolve).ToArray();

        /// <summary>Writes one row. Non-numeric values (text, timestamp, bytea, ...) match their CLR type, so inference is right for them.</summary>
        public static async Task WriteRowAsync(NpgsqlBinaryImporter importer, object?[] row, PostgresWritePlan[] plans, CancellationToken ct)
        {
            await importer.StartRowAsync(ct);
            for (var i = 0; i < row.Length; i++)
            {
                var value = row[i];
                if (value == null || value == DBNull.Value)
                {
                    await importer.WriteNullAsync(ct);
                    continue;
                }

                var plan = i < plans.Length ? plans[i] : default;
                if (plan.Typed) await importer.WriteAsync(plan.Coerce(value), plan.DbType, ct);
                else await importer.WriteAsync(value, ct);
            }
        }
    }
}
