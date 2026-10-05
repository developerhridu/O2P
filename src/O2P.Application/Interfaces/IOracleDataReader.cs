using O2P.Application.Copying;
using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IOracleDataReader
    {
        /// <param name="progress">
        /// Told about every row, with how long the read waited on Oracle and on the writer. Optional.
        /// </param>
        /// <remarks>
        /// Always completes <paramref name="outputChannel"/> - with the error if the read fails - so the
        /// writer never waits for rows that are not coming.
        /// </remarks>
        Task ReadChunkDataAsync(Connection connection, string password, string owner, string tableName, IReadOnlyList<ManifestColumn> columns, string? whereClause, ChunkLog chunk, ChannelWriter<object[]> outputChannel, IRateLimiter? rateLimiter, ChunkProgress? progress, CancellationToken cancellationToken);
    }
}
