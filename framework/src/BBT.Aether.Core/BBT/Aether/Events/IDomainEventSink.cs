using System.Collections.Generic;

namespace BBT.Aether.Events;

/// <summary>
/// Represents a sink for domain events collected from entities.
/// Allows DbContext to publish domain events without direct knowledge of Unit of Work.
/// </summary>
public interface IDomainEventSink
{
    /// <summary>
    /// Enqueues domain events for later dispatching.
    /// Typically called by DbContext during SaveChanges to push events to the active Unit of Work.
    /// </summary>
    /// <param name="events">The domain event envelopes to enqueue</param>
    void EnqueueDomainEvents(IEnumerable<DomainEventEnvelope> events);
}

