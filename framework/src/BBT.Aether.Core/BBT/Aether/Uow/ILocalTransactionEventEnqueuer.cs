using System.Collections.Generic;
using BBT.Aether.Events;

namespace BBT.Aether.Uow;

/// <summary>
/// Interface for local transactions that support event enqueueing.
/// Allows DbContext to push events directly into its owning local transaction,
/// ensuring events are routed to the correct Unit of Work regardless of ambient context.
/// </summary>
public interface ILocalTransactionEventEnqueuer
{
    /// <summary>
    /// Enqueues domain events that were collected by DbContext during SaveChanges.
    /// Events pushed here will be dispatched when the owning Unit of Work commits.
    /// </summary>
    /// <param name="events">The events to enqueue</param>
    void EnqueueEvents(IEnumerable<DomainEventEnvelope> events);
}

