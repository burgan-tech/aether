using System.Collections.Generic;
using BBT.Aether.Events;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Interface for entities that can raise domain events.
/// Typically implemented by aggregate roots to collect distributed events for later dispatching.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>
    /// Gets all domain events that have been raised by this entity.
    /// Events are wrapped with their metadata for proper dispatching.
    /// </summary>
    IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents();

    /// <summary>
    /// Clears all domain events from this entity.
    /// Called after events have been dispatched.
    /// </summary>
    void ClearDomainEvents();
}

