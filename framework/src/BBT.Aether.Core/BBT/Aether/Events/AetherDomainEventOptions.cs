namespace BBT.Aether.Events;

/// <summary>
/// Configuration options for domain event dispatching.
/// </summary>
public class AetherDomainEventOptions
{
    /// <summary>
    /// Gets or sets whether to write events to the outbox when publishing to broker fails.
    /// Default is false (events are lost on publish failure).
    /// When true, failed events are stored in the outbox for retry by the OutboxProcessor.
    /// </summary>
    public bool WriteToOutboxOnPublishError { get; set; } = false;
}

