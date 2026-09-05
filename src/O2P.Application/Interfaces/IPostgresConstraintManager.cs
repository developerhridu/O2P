using O2P.Application.Schema;
using O2P.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Interfaces
{
    public interface IPostgresConstraintManager
    {
        Task<bool> TableExistsAsync(Connection connection, string password, string schema, string table, CancellationToken cancellationToken);

        Task<PostgresConstraintSnapshot> SnapshotAsync(Connection connection, string password, string schema, string table, CancellationToken cancellationToken);

        Task DropAsync(Connection connection, string password, PostgresConstraintSnapshot snapshot, CancellationToken cancellationToken);

        Task RestoreAsync(Connection connection, string password, PostgresConstraintSnapshot snapshot, CancellationToken cancellationToken);

        Task TruncateAsync(Connection connection, string password, string schema, string table, CancellationToken cancellationToken);
    }
}
