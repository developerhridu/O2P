using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IPostgresDdlExecutor
    {
        Task ExecuteDdlAsync(Connection connection, string password, string ddlScript, CancellationToken cancellationToken);
    }
}
