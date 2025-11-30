using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for distributed event publishing. Events are automatically wrapped in CloudEventEnvelope format.
/// </summary>
public interface IDistributedEventBus : IEventBus
{
    /// <summary>
    /// Publishes an event with outbox support. The event payload is automatically wrapped in a CloudEventEnvelope.
    /// </summary>
    /// <typeparam name="TEvent">The event data type</typeparam>
    /// <param name="payload">The event payload</param>
    /// <param name="subject">Optional subject identifier (e.g., aggregate ID)</param>
    /// <param name="useOutbox">Whether to use the outbox pattern for transactional publishing</param>
    /// <param name="cancellationToken"></param>
    Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        bool useOutbox = true, 
        CancellationToken cancellationToken = default)
        where TEvent : class;

    /// <summary>
    /// Publishes an event using pre-extracted metadata from EventNameAttribute.
    /// This method is primarily used by the domain event dispatcher to avoid reflection during dispatching.
    /// </summary>
    /// <param name="event">The event to publish</param>
    /// <param name="metadata">Pre-extracted event metadata (EventName, Version, PubSubName, etc.)</param>
    /// <param name="subject">Optional subject identifier (e.g., aggregate ID)</param>
    /// <param name="useOutbox">Whether to use the outbox pattern for transactional publishing</param>
    /// <param name="cancellationToken"></param>
    Task PublishAsync(
        IDistributedEvent @event,
        EventMetadata metadata,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a pre-serialized CloudEventEnvelope directly to the broker.
    /// Used internally by the outbox processor to republish stored events.
    /// </summary>
    /// <param name="serializedEnvelope">The serialized CloudEventEnvelope (JSON bytes)</param>
    /// <param name="topicName">The topic name to publish to</param>
    /// <param name="pubSubName">The PubSub component name</param>
    /// <param name="cancellationToken"></param>
    Task PublishEnvelopeAsync(
        byte[] serializedEnvelope,
        string topicName,
        string pubSubName,
        CancellationToken cancellationToken = default);
}
