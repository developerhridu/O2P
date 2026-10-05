using O2P.Application.Schema;
using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    /// <summary>
    /// Read-only introspection of a target Postgres schema. Deliberately separate from
    /// IPostgresConstraintManager, which is a constraint-lifecycle abstraction: that interface
    /// no-ops for mock connections, and there is no correct neutral value for column introspection
    /// (an empty list would report every manifest column as missing).
    /// </summary>
    /// <param name="Name">Schema name, exactly as Postgres stores it.</param>
    /// <param name="CanCreate">
    /// Whether this account may create tables in it. A run only needs this for tables that do not
    /// exist yet, so a read-only schema is still usable - it is shown, just marked.
    /// </param>
    public sealed record TargetSchema(string Name, bool CanCreate);

    public interface IPostgresSchemaInspector
    {
        /// <summary>
        /// Schemas in the destination database that this account can use, for the destination
        /// picker. Postgres' own schemas (pg_*, information_schema) are left out.
        /// </summary>
        Task<IReadOnlyList<TargetSchema>> ListSchemasAsync(
            Connection connection,
            string password,
            CancellationToken cancellationToken);

        /// <summary>
        /// Columns as they actually exist in the target table, or null when the target cannot be
        /// introspected at all (mock connections). Callers must skip compatibility checking on null
        /// rather than treat it as "no columns", which would report every manifest column as missing.
        /// </summary>
        Task<IReadOnlyList<PostgresLiveColumn>?> GetTableColumnsAsync(
            Connection connection,
            string password,
            string schema,
            string table,
            CancellationToken cancellationToken);

        /// <summary>
        /// Which of <paramref name="tableNames"/> already exist in the schema, in one round-trip.
        /// Returns an empty set for mock connections.
        /// </summary>
        Task<IReadOnlyCollection<string>> GetExistingTableNamesAsync(
            Connection connection,
            string password,
            string schema,
            IReadOnlyCollection<string> tableNames,
            CancellationToken cancellationToken);

        /// <summary>
        /// Every table in the schema, for the Dashboard's row-count comparison: ordinary tables and
        /// partitioned tables (once - their partitions are left out, the parent's count covers them).
        /// O2P's own resume-fence table is left out. Returns two made-up tables for mock connections.
        /// </summary>
        Task<IReadOnlyList<string>> ListTableNamesAsync(
            Connection connection,
            string password,
            string schema,
            CancellationToken cancellationToken);
    }
}
