using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IPostgresBinaryWriter
    {
        Task<long> WriteDataAsync(Connection connection, string password, string targetSchema, string targetTable, IReadOnlyList<ManifestColumn> columns, long jobRunId, long tableRunId, int chunkIndex, ChannelReader<object[]> inputChannel, CancellationToken cancellationToken);
    }
}
