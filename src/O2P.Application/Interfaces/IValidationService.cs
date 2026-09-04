using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IValidationService
    {
        Task<ValidationResult> ValidateTableRunAsync(TableRun tableRun, Connection sourceConnection, string sourcePassword, Connection targetConnection, string targetPassword, CancellationToken cancellationToken);
    }
}
