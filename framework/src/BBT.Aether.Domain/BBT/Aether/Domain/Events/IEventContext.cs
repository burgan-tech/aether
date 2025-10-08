using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events.Distributed;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Provides a unified context for handling different types of domain events.
/// This interface abstracts the complexity of managing pre-commit, post-commit, and distributed events.
/// </summary>
public interface IEventContext
{
    /// <summary>
    /// Dispatches pre-commit events that should be processed within the same transaction.
    /// </summary>
    /// <param name="events">The pre-commit events to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DispatchPreCommitEventsAsync(IEnumerable<IPreCommitEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches post-commit events that should be processed after successful database commit.
    /// </summary>
    /// <param name="events">The post-commit events to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DispatchPostCommitEventsAsync(IEnumerable<IPostCommitEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes distributed events to external systems.
    /// </summary>
    /// <param name="events">The distributed events to publish.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PublishDistributedEventsAsync(IEnumerable<IDistributedDomainEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores post-commit events for later dispatch (used in transaction scenarios).
    /// </summary>
    /// <param name="events">The post-commit events to store.</param>
    void StorePostCommitEvents(IEnumerable<IPostCommitEvent> events);

    /// <summary>
    /// Stores distributed events for later publishing (used in transaction scenarios).
    /// </summary>
    /// <param name="events">The distributed events to store.</param>
    void StoreDistributedEvents(IEnumerable<IDistributedDomainEvent> events);

    /// <summary>
    /// Dispatches all stored events after successful transaction commit.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DispatchStoredEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all stored events (used after dispatch or on rollback).
    /// </summary>
    void ClearStoredEvents();

    /// <summary>
    /// Gets whether there are any stored events waiting to be processed.
    /// </summary>
    bool HasStoredEvents { get; }
}
