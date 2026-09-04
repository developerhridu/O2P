using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IOracleDiscoveryService
    {
        Task<IEnumerable<DiscoveryCache>> DiscoverTablesAsync(Connection connection, string password, string owner, CancellationToken cancellationToken, IReadOnlyCollection<string>? tableNames = null);
    }
}
