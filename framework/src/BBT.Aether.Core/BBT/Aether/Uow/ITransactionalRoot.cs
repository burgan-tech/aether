using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a root Unit of Work that supports lazy transaction escalation.
/// Allows a reserved (non-transactional) UoW to be escalated to transactional mode.
/// </summary>
public interface ITransactionalRoot
{
    /// <summary>
    /// Ensures that a transaction is started for this Unit of Work if not already started.
    /// This allows lazy escalation from a reserved (non-transactional) UoW to a transactional one.
    /// </summary>
    /// <param name="isolationLevel">Optional isolation level for the transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default);
}

