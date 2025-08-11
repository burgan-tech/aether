using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Defines an interface for supporting saving changes.
/// </summary>
public interface ISupportsSavingChanges
{
    /// <summary>
    /// Saves changes asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}