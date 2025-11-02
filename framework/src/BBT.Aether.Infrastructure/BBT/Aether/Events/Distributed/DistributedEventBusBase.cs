using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Events;

public abstract class DistributedEventBusBase(
    ITopicNameStrategy topicNameStrategy,
    IEventSerializer eventSerializer,
    IServiceScopeFactory serviceScopeFactory,
    AetherEventBusOptions eventBusOptions)
    : IDistributedEventBus
{
    protected readonly ITopicNameStrategy TopicNameStrategy = topicNameStrategy;
    protected readonly IEventSerializer EventSerializer = eventSerializer;
    protected readonly IServiceScopeFactory ServiceScopeFactory = serviceScopeFactory;
    protected readonly AetherEventBusOptions AetherEventBusOptions = eventBusOptions;

    /// <summary>
    /// Creates a CloudEventEnvelope from the event payload.
    /// The Type is generated from EventNameAttribute on the event type using TopicNameStrategy.
    /// The Source is populated from AetherEventBusOptions.DefaultSource.
    /// </summary>
    protected CloudEventEnvelope<TEvent> CreateEnvelope<TEvent>(
        TEvent payload,
        string? subject = null) where TEvent : class
    {
        var type = TopicNameStrategy.GetTopicName(typeof(TEvent));
        var source = AetherEventBusOptions.DefaultSource
            ?? throw new InvalidOperationException(
                "DefaultSource must be configured in AetherEventBusOptions. Set AetherEventBusOptions.DefaultSource when configuring the EventBus.");

        return new CloudEventEnvelope<TEvent>
        {
            Type = type,
            Source = source,
            Subject = subject,
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
            await StoreInOutboxAsync(typeof(TEvent), envelope, cancellationToken);
        }
        else
        {
            // Serialize envelope using IEventSerializer for consistent format
            var serialized = EventSerializer.Serialize(envelope);
            await PublishToBrokerAsync<TEvent>(topicName, serialized, cancellationToken);
        }
    }

    /// <summary>
    /// Stores the event in outbox using a scoped IOutboxStore instance.
    /// This ensures that scoped services (like DbContext) are properly resolved per operation.
    /// </summary>
    private async Task StoreInOutboxAsync(Type eventType, object envelope, CancellationToken cancellationToken)
    {
        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await outboxStore.StoreAsync(eventType, envelope, cancellationToken);
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
}
