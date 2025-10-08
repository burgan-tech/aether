using System;

namespace BBT.Aether.Domain.Events.Distributed;

/// <summary>
/// Base interface for distributed domain events.
/// These events are published to external systems (message brokers, event stores, etc.)
/// </summary>
public interface IDistributedDomainEvent
{
    /// <summary>
    /// Gets the unique identifier for this event.
    /// </summary>
    string EventId { get; }
    
    /// <summary>
    /// Gets the date and time when the event occurred.
    /// </summary>
    DateTime OccurredOn { get; }
    
    /// <summary>
    /// Gets the event type name for routing and deserialization.
    /// </summary>
    string EventType { get; }
    
    /// <summary>
    /// Gets the aggregate ID that raised this event.
    /// </summary>
    string AggregateId { get; }
    
    /// <summary>
    /// Gets the aggregate type that raised this event.
    /// </summary>
    string AggregateType { get; }
    
    /// <summary>
    /// Gets the version of the aggregate when this event was raised.
    /// </summary>
    long AggregateVersion { get; }
}
