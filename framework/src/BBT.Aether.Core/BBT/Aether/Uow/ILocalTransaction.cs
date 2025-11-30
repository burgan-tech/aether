using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;

namespace BBT.Aether.Uow;

/// <summary>
/// Represents a local transaction within a specific data source.
/// Coordinated by CompositeUnitOfWork.
/// </summary>
public interface ILocalTransaction
{
    /// <summary>
    /// Gets the domain events collected during this transaction.
    /// Events are automatically pushed via IDomainEventSink during SaveChanges.
    /// </summary>
    IReadOnlyList<DomainEventEnvelope> CollectedEvents { get; }

    /// <summary>
    /// Commits the local transaction, persisting all changes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the local transaction, undoing all changes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears collected domain events after successful dispatch.
    /// Called by CompositeUnitOfWork after events are dispatched.
    /// </summary>
    void ClearCollectedEvents();
}

