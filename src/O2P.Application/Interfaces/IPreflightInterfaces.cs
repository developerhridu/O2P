using O2P.Domain.Entities;
using O2P.Application.Validation;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IPreflightValidatorService
    {
        /// <summary>
        /// <paramref name="targetTableNames"/> is the job's target tables, used to decide whether
        /// CREATE privileges are actually needed. Pass an empty collection when that is unknown, and
        /// CREATE stays mandatory.
        /// </summary>
        Task<PreflightResult> RunPreflightChecksAsync(
            Connection sourceConnection,
            string sourcePassword,
            Connection targetConnection,
            string targetPassword,
            string targetSchema,
            IReadOnlyCollection<string> targetTableNames,
            CancellationToken cancellationToken);
    }

    public interface ISourcePreflightExecutor
    {
        Task<bool> CheckPrivilegesAsync(Connection sourceConnection, string password, CancellationToken cancellationToken);
    }

    public interface ITargetPreflightExecutor
    {
        Task<bool> CheckVersionAsync(Connection targetConnection, string password, CancellationToken cancellationToken);
        /// <summary>Checks USAGE and CREATE together.</summary>
        Task<bool> CheckSchemaPrivilegesAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken);

        /// <summary>USAGE only - the minimum needed to COPY into an existing table.</summary>
        Task<bool> CheckSchemaUsageAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken);
        Task<bool> RunProbeTableAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken);
    }
}
