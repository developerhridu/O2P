using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface ISourceCountExecutor
    {
        Task<long> GetRowCountAsync(Connection sourceConnection, string password, string owner, string tableName, string? whereClause, CancellationToken cancellationToken);
    }
}
