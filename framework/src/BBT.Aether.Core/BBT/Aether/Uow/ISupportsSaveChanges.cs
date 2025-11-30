using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Interface for transaction sources that support explicit SaveChanges.
/// Allows saving changes without committing the transaction.
/// </summary>
public interface ISupportsSaveChanges
{
    /// <summary>
    /// Saves changes to the underlying store without committing the transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

