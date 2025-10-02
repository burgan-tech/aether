using System.Collections.Generic;
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Events.Distributed;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Defines a service for storing domain events during transaction processing.
/// Events stored here will be dispatched after successful transaction commit.
/// </summary>
public interface ITransactionEventStorage
{
    /// <summary>
    /// Stores post-commit events to be dispatched after transaction commit.
    /// </summary>
    /// <param name="events">The post-commit events to store.</param>
    void StorePostCommitEvents(IEnumerable<IPostCommitEvent> events);

    /// <summary>
    /// Stores distributed events to be published after transaction commit.
    /// </summary>
    /// <param name="events">The distributed events to store.</param>
    void StoreDistributedEvents(IEnumerable<IDistributedDomainEvent> events);

    /// <summary>
    /// Gets whether there are any stored events waiting to be processed.
    /// </summary>
    bool HasStoredEvents { get; }
}
