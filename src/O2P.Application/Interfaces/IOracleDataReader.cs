using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IOracleDataReader
    {
        Task ReadChunkDataAsync(Connection connection, string password, string owner, string tableName, IReadOnlyList<ManifestColumn> columns, string? whereClause, ChunkLog chunk, ChannelWriter<object[]> outputChannel, IRateLimiter? rateLimiter, CancellationToken cancellationToken);
    }
}
