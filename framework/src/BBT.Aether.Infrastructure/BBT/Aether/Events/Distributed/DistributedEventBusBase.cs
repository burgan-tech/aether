using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public abstract class DistributedEventBusBase(
    ITopicNameStrategy topicNameStrategy,
    IEventSerializer eventSerializer,
    IOutboxStore outboxStore,
    AetherEventBusOptions eventBusOptions)
    : IDistributedEventBus
{
    protected readonly ITopicNameStrategy TopicNameStrategy = topicNameStrategy;
    protected readonly IEventSerializer EventSerializer = eventSerializer;
    protected readonly IOutboxStore OutboxStore = outboxStore;
    protected readonly AetherEventBusOptions AetherEventBusOptions = eventBusOptions;

    /// <summary>
    /// Creates a CloudEventEnvelope from the event payload using EventMeta&lt;T&gt;.
    /// The Type is generated from EventNameAttribute on the event type using TopicNameStrategy.
    /// The Source is populated from AetherEventBusOptions.DefaultSource.
    /// If subject is null, it will attempt to extract it from properties marked with [EventSubject] attribute.
    /// </summary>
    protected CloudEventEnvelope CreateEnvelope<TEvent>(
        TEvent payload,
        string? subject = null) where TEvent : class
    {
        var type = TopicNameStrategy.GetTopicName(typeof(TEvent));
        var source = AetherEventBusOptions.DefaultSource
            ?? throw new InvalidOperationException(
                "DefaultSource must be configured in AetherEventBusOptions. Set AetherEventBusOptions.DefaultSource when configuring the EventBus.");

        // If subject is not provided, try to extract it from [EventSubject] attribute
        subject ??= EventSubjectExtractor.ExtractSubject(payload);

        return new CloudEventEnvelope
        {
            Type = type,
            Source = source,
            Subject = subject,
            DataSchema = EventMeta<TEvent>.DataSchema,
            Data = payload
            // Other properties (SpecVersion, Id, Time, DataContentType) use defaults from CloudEventEnvelope class
        };
    }

    // IEventBus implementations
    public Task PublishAsync<TEvent>(TEvent payload, string? subject = null, CancellationToken cancellationToken = default) where TEvent : class
    {
        return PublishAsync(payload, subject, useOutbox: true, cancellationToken);
    }

    // IDistributedEventBus implementations
    public async Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        // Create envelope from payload
        var envelope = CreateEnvelope(payload, subject);
        // Use TopicNameStrategy to get topic name (includes environment prefix if enabled)
        var topicName = envelope.Type;

        if (useOutbox)
        {
            await StoreInOutboxAsync(envelope, cancellationToken);
        }
        else
        {
            // Serialize envelope using IEventSerializer for consistent format
            var serialized = EventSerializer.Serialize(envelope);
            await PublishToBrokerAsync<TEvent>(topicName, serialized, cancellationToken);
        }
    }

    // Metadata-based publish for domain events (no reflection needed)
    public async Task PublishAsync(
        IDistributedEvent @event,
        EventMetadata metadata,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default)
    {
        // Create envelope using metadata - no reflection needed
        var envelope = CreateEnvelopeFromMetadata(@event, metadata, subject);
        
        if (useOutbox)
        {
            await StoreInOutboxAsync(envelope, cancellationToken);
        }
        else
        {
            var serialized = EventSerializer.Serialize(envelope);
            // Use PubSubName from metadata or fall back to default from options
            var pubSubName = metadata.PubSubName ?? AetherEventBusOptions.PubSubName;
            await PublishToBrokerAsync(envelope.Type, pubSubName, serialized, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a CloudEventEnvelope from an event and its pre-extracted metadata.
    /// No reflection needed - directly constructs the envelope.
    /// If subject is null, it will attempt to extract it from properties marked with [EventSubject] attribute.
    /// </summary>
    private CloudEventEnvelope CreateEnvelopeFromMetadata(IDistributedEvent @event, EventMetadata metadata, string? subject)
    {
        // Get topic name using TopicNameStrategy (handles environment prefixing)
        var type = TopicNameStrategy.GetTopicName(metadata.EventType);
        var source = AetherEventBusOptions.DefaultSource
            ?? throw new InvalidOperationException(
                "DefaultSource must be configured in AetherEventBusOptions. Set AetherEventBusOptions.DefaultSource when configuring the EventBus.");

        // If subject is not provided, try to extract it from [EventSubject] attribute
        subject ??= EventSubjectExtractor.ExtractSubject(@event);

        return new CloudEventEnvelope
        {
            Type = type,
            Source = source,
            Subject = subject,
            DataSchema = metadata.DataSchema,
            Data = @event
            // Other properties (SpecVersion, Id, Time, DataContentType) use defaults from CloudEventEnvelope class
        };
    }

    /// <summary>
    /// Stores the event in outbox using the injected IOutboxStore instance.
    /// Store only tracks the entity; SaveChanges will be called by UoW or calling code.
    /// </summary>
    private async Task StoreInOutboxAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken)
    {
        await OutboxStore.StoreAsync(envelope, cancellationToken);
        // No SaveChanges here - will be flushed by UoW Commit or calling code
    }
    
    /// <summary>
    /// Publishes a pre-serialized CloudEventEnvelope directly to the broker.
    /// Used internally by the outbox processor to republish stored events.
    /// </summary>
    public Task PublishEnvelopeAsync(
        byte[] serializedEnvelope,
        string topicName,
        string pubSubName,
        CancellationToken cancellationToken = default)
    {
        return PublishToBrokerAsync(topicName, pubSubName, serializedEnvelope, cancellationToken);
    }
    
    /// <summary>
    /// Publishes the serialized CloudEventEnvelope to the broker.
    /// The envelope is serialized using IEventSerializer to ensure consistent format with handler deserialization.
    /// </summary>
    /// <typeparam name="TEvent">The event type (used by implementations to resolve broker-specific settings)</typeparam>
    /// <param name="topic">The topic name to publish to</param>
    /// <param name="serializedEnvelope">The serialized CloudEventEnvelope</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope, CancellationToken cancellationToken = default) where TEvent : class;
    
    /// <summary>
    /// Publishes the serialized CloudEventEnvelope to the broker without generic type information.
    /// Used by PublishEnvelopeAsync for outbox processing.
    /// </summary>
    /// <param name="topic">The topic name to publish to</param>
    /// <param name="pubSubName">The PubSub component name</param>
    /// <param name="serializedEnvelope">The serialized CloudEventEnvelope</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task PublishToBrokerAsync(string topic, string pubSubName, byte[] serializedEnvelope, CancellationToken cancellationToken = default);
}
