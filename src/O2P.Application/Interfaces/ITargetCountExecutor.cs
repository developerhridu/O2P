using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface ITargetCountExecutor
    {
        Task<long> GetRowCountAsync(Connection targetConnection, string password, string schema, string tableName, CancellationToken cancellationToken);
    }
}
