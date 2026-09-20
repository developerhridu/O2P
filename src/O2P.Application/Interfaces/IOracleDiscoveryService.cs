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

    public interface IOracleDiscoveryService
    {
        Task<IEnumerable<DiscoveryCache>> DiscoverTablesAsync(Connection connection, string password, string owner, CancellationToken cancellationToken, IReadOnlyCollection<string>? tableNames = null);

        /// <summary>
        /// Lists the schemas that own at least one table the account can read, with Oracle's
        /// built-in system schemas removed. Counts use the same table filter as
        /// <see cref="DiscoverTablesAsync"/>, so the number shown is what a scan will find.
        /// </summary>
        Task<SourceSchemaList> ListSchemasAsync(Connection connection, string password, CancellationToken cancellationToken);
    }
}
