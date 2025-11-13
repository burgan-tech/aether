namespace BBT.Aether.Events;

/// <summary>
/// Defines the strategy for dispatching domain events.
/// </summary>
public enum DomainEventDispatchStrategy
{
    /// <summary>
    /// Always write events to outbox within the transaction.
    /// Events are dispatched by the OutboxProcessor.
    /// This provides maximum reliability as events are persisted atomically with business data.
    /// </summary>
    AlwaysUseOutbox,
    
    /// <summary>
    /// Publish events directly after commit.
    /// On publish failure, write to outbox in a new scope.
    /// This provides lower latency but requires the broker to be available.
    /// </summary>
    PublishWithFallback
}

/// <summary>
/// Configuration options for domain event dispatching.
/// </summary>
public class AetherDomainEventOptions
{
    /// <summary>
    /// Gets or sets the dispatch strategy for domain events.
    /// Default is AlwaysUseOutbox for maximum reliability.
    /// </summary>
    public DomainEventDispatchStrategy DispatchStrategy { get; set; } = DomainEventDispatchStrategy.AlwaysUseOutbox;
}

