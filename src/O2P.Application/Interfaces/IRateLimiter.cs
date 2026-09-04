using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IRateLimiter
    {
        Task WaitAsync(int tokens, CancellationToken cancellationToken);
    }
}
