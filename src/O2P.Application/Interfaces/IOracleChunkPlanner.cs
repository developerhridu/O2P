using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IOracleChunkPlanner
    {
        Task<IEnumerable<ChunkLog>> PlanChunksAsync(Connection connection, string password, string owner, string tableName, bool isPartitioned, bool isIot, int estimatedChunks, CancellationToken cancellationToken);
    }
}
