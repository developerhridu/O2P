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
    public interface IPostgresSchemaInspector
    {
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
    }
}
