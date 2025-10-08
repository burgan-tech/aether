using System;

namespace BBT.Aether.Domain.Events.Distributed;

/// <summary>
/// Base class for distributed domain events with common properties.
/// </summary>
public abstract class DistributedDomainEventBase : IDistributedDomainEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedDomainEventBase"/> class.
    /// </summary>
    /// <param name="aggregateId">The ID of the aggregate that raised this event.</param>
    /// <param name="aggregateType">The type of the aggregate that raised this event.</param>
    /// <param name="aggregateVersion">The version of the aggregate when this event was raised.</param>
    protected DistributedDomainEventBase(string aggregateId, string aggregateType, long aggregateVersion)
    {
        EventId = Guid.NewGuid().ToString();
        OccurredOn = DateTime.UtcNow;
        EventType = GetType().Name;
        AggregateId = aggregateId ?? throw new ArgumentNullException(nameof(aggregateId));
        AggregateType = aggregateType ?? throw new ArgumentNullException(nameof(aggregateType));
        AggregateVersion = aggregateVersion;
    }

    /// <inheritdoc />
    public string EventId { get; }

    /// <inheritdoc />
    public DateTime OccurredOn { get; }

    /// <inheritdoc />
    public string EventType { get; }

    /// <inheritdoc />
    public string AggregateId { get; }

    /// <inheritdoc />
    public string AggregateType { get; }

    /// <inheritdoc />
    public long AggregateVersion { get; }
}
