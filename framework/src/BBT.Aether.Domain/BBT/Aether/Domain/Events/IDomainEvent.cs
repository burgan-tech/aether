using System;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Base interface for domain events.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Gets the date and time when the event occurred.
    /// </summary>
    DateTime OccurredOn { get; }
}
