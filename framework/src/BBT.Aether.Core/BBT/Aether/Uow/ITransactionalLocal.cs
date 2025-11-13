using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a local transaction that supports lazy transaction escalation.
/// Allows a reserved (non-transactional) local transaction to be escalated to transactional mode.
/// </summary>
public interface ITransactionalLocal
{
    /// <summary>
    /// Ensures that a transaction is started for this local transaction if not already started.
    /// This allows lazy escalation from a reserved (non-transactional) state to a transactional one.
    /// </summary>
    /// <param name="isolationLevel">Optional isolation level for the transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnsureTransactionAsync(IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = default);
}

