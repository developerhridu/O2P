using O2P.Domain.Entities;
using O2P.Application.Validation;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IPreflightValidatorService
    {
        Task<PreflightResult> RunPreflightChecksAsync(Connection sourceConnection, string sourcePassword, Connection targetConnection, string targetPassword, string targetSchema, CancellationToken cancellationToken);
    }

    public interface ISourcePreflightExecutor
    {
        Task<bool> CheckPrivilegesAsync(Connection sourceConnection, string password, CancellationToken cancellationToken);
    }

    public interface ITargetPreflightExecutor
    {
        Task<bool> CheckVersionAsync(Connection targetConnection, string password, CancellationToken cancellationToken);
        Task<bool> CheckSchemaPrivilegesAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken);
        Task<bool> RunProbeTableAsync(Connection targetConnection, string password, string schema, CancellationToken cancellationToken);
    }
}
