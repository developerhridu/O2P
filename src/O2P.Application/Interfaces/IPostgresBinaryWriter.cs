using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IPostgresBinaryWriter
    {
        /// <param name="targetNameStyle">
        /// How to spell the destination column names: "lower" for a table O2P created, "source" to keep
        /// Oracle's own spelling for a table an earlier run left behind. Resolved once when the table
        /// is prepared and read back from TableRun for every batch, so all the batches of one run agree.
        /// </param>
        Task<long> WriteDataAsync(Connection connection, string password, string targetSchema, string targetTable, IReadOnlyList<ManifestColumn> columns, string? targetNameStyle, long jobRunId, long tableRunId, int chunkIndex, ChannelReader<object[]> inputChannel, CancellationToken cancellationToken);
    }
}
