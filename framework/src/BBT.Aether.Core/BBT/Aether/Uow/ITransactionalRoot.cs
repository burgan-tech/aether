using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a root Unit of Work that can ensure its configured transaction is started lazily.
/// </summary>
public interface ITransactionalRoot
{
    /// <summary>
    /// Ensures that the transaction configured when this Unit of Work began is started, if needed.
    /// </summary>
    /// <param name="isolationLevel">Optional isolation level for the transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default);
}
