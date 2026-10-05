using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    /// <summary>A source schema that owns tables the connected account can read.</summary>
    public sealed record SourceSchema(string Name, int TableCount);

    /// <param name="Schemas">Schemas a scan can actually handle, ordered by name.</param>
    /// <param name="Skipped">
    /// Schemas that own readable tables but were left out because O2P cannot scan their names
    /// (mixed-case or otherwise unsafe identifiers). Reported so the UI can say so.
    /// </param>
    public sealed record SourceSchemaList(IReadOnlyList<SourceSchema> Schemas, int Skipped);

    /// <param name="Bytes">Table size, or null when no source for it was readable.</param>
    /// <param name="IsEstimate">True when it was estimated from statistics rather than measured.</param>
    public sealed record TableSize(long? Bytes, bool IsEstimate);

    public interface IOracleDiscoveryService
    {
        Task<IEnumerable<DiscoveryCache>> DiscoverTablesAsync(Connection connection, string password, string owner, CancellationToken cancellationToken, IReadOnlyCollection<string>? tableNames = null);

        /// <summary>
        /// Lists the schemas that own at least one table the account can read, with Oracle's
        /// built-in system schemas removed. Counts use the same table filter as
        /// <see cref="DiscoverTablesAsync"/>, so the number shown is what a scan will find.
        /// </summary>
        Task<SourceSchemaList> ListSchemasAsync(Connection connection, string password, CancellationToken cancellationToken);

        /// <summary>
        /// Current size of one table, read the same way <see cref="DiscoverTablesAsync"/> reads it.
        /// Used by the per-table Sync so a size can be refreshed without rescanning the schema.
        /// </summary>
        Task<TableSize> GetTableSizeAsync(Connection connection, string password, string owner, string tableName, CancellationToken cancellationToken);

        /// <summary>
        /// Names of the tables in one schema, and nothing else - no columns, sizes or statistics, so it is
        /// quick on a schema of thousands of tables. Same filter as <see cref="DiscoverTablesAsync"/>, less
        /// tables in the recycle bin and temporary tables, whose rows belong to a session and cannot be
        /// compared. For the Dashboard's row-count comparison.
        /// </summary>
        Task<IReadOnlyList<string>> ListTableNamesAsync(Connection connection, string password, string owner, CancellationToken cancellationToken);
    }
}
