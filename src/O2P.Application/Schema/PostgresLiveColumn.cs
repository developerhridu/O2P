namespace O2P.Application.Schema
{
    /// <summary>
    /// A column as it actually exists in the target Postgres table, read from pg_attribute.
    /// <see cref="FormattedType"/> is format_type(atttypid, atttypmod) output, e.g.
    /// "character varying(255)", "numeric(10,2)", "timestamp(0) without time zone".
    /// </summary>
    public sealed record PostgresLiveColumn(
        string ColumnName,
        string FormattedType,
        bool NotNull,
        bool HasDefault,
        bool IsIdentity,
        bool IsGenerated)
    {
        /// <summary>
        /// True when Postgres fills this column in by itself if COPY leaves it out of the column list.
        /// atthasdef alone is not enough: it is false for GENERATED ALWAYS AS IDENTITY columns.
        /// </summary>
        public bool IsSelfPopulating => HasDefault || IsIdentity || IsGenerated;
    }
}
