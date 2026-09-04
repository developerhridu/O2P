using System;

namespace O2P.Application.Schema
{
    public static class TypeMapper
    {
        public static string MapOracleToPostgres(string oracleType, int? precision, int? scale)
        {
            return MapOracleToPostgres(oracleType, null, precision, scale);
        }

        public static string MapOracleToPostgres(string oracleType, int? length, int? precision, int? scale)
        {
            var upperType = oracleType.Trim().ToUpperInvariant().Split('(')[0];

            switch (upperType)
            {
                case "VARCHAR2":
                case "NVARCHAR2":
                    if (length.HasValue && length.Value > 0 && length.Value <= 10485760)
                    {
                        return $"varchar({length.Value})";
                    }
                    return "text";
                case "CHAR":
                case "NCHAR":
                    if (length.HasValue && length.Value > 0 && length.Value <= 10485760)
                    {
                        return $"char({length.Value})";
                    }
                    return "text";
                case "CLOB":
                case "NCLOB":
                case "LONG":
                    return "text";

                case "NUMBER":
                    if (scale == 0)
                    {
                        if (precision <= 4) return "smallint";
                        if (precision <= 9) return "integer";
                        if (precision <= 18) return "bigint";
                        if (precision.HasValue) return $"numeric({precision.Value})";
                    }
                    if (precision.HasValue && scale.HasValue)
                    {
                        return $"numeric({precision.Value}, {scale.Value})";
                    }
                    return "numeric";

                case "DATE":
                    return "timestamp(0) without time zone";

                case "TIMESTAMP":
                    return "timestamp without time zone";

                case "TIMESTAMP WITH TIME ZONE":
                case "TIMESTAMP WITH LOCAL TIME ZONE":
                    return "timestamp with time zone";

                case "BLOB":
                case "BFILE":
                case "RAW":
                case "LONG RAW":
                    return "bytea";

                case "BINARY_FLOAT":
                    return "real";

                case "FLOAT":
                case "BINARY_DOUBLE":
                    return "double precision";

                case "ROWID":
                case "UROWID":
                    return "varchar(4000)";

                case "XMLTYPE":
                    return "xml";

                case "INTERVAL YEAR TO MONTH":
                case "INTERVAL DAY TO SECOND":
                    return "interval";

                default:
                    // Fallback for unknown types
                    return "text";
            }
        }
    }
}
