namespace BBT.Aether.Events;

/// <summary>
/// Wraps a distributed event with its metadata for proper dispatching.
/// Metadata is extracted once at the time of AddDistributedEvent.
/// </summary>
public sealed class DomainEventEnvelope(IDistributedEvent @event, EventMetadata metadata)
{
    /// <summary>
    /// Gets the distributed event.
    /// </summary>
    public IDistributedEvent Event { get; } = @event;

    /// <summary>
    /// Gets the event metadata (topic name, PubSub name, version).
    /// </summary>
    public EventMetadata Metadata { get; } = metadata;
}

