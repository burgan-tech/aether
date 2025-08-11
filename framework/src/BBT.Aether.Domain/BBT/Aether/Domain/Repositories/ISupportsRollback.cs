using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Defines an interface for supporting rollback operations.
/// </summary>
public interface ISupportsRollback
{
    /// <summary>
    /// Rolls back changes asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}