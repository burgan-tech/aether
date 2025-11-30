using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using Dapr.Client;

namespace BBT.Aether.Events;

public class DaprEventBus(
    DaprClient daprClient,
    AetherEventBusOptions options,
    ITopicNameStrategy topicNameStrategy,
    IEventSerializer eventSerializer,
    IOutboxStore outboxStore,
    ICurrentSchema currentSchema)
    : DistributedEventBusBase(topicNameStrategy, eventSerializer, outboxStore, options, currentSchema)
{
    private readonly AetherEventBusOptions _options = options;

    /// <summary>
    /// Resolves the PubSub component name for an event type.
    /// Uses EventNameInfo helper to extract PubSubName from attribute, falls back to default from options.
    /// </summary>
    private string ResolvePubSubName<TEvent>() where TEvent : class
    {
        var eventInfo = EventNameAttribute.GetEventNameInfo(typeof(TEvent));
        return eventInfo.PubSubName ?? _options.PubSubName;
    }

    protected override Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
    {
        // Resolve PubSub component name from event type attribute or default
        var pubSubName = ResolvePubSubName<TEvent>();

        // Deserialize the envelope to object so Dapr can serialize it properly
        // This prevents double-serialization (string wrapping) by Dapr
        var envelope = EventSerializer.Deserialize<object>(serializedEnvelope);
        
        // Publish as object - Dapr will serialize it as JSON object (not string-wrapped)
        return daprClient.PublishEventAsync(pubSubName, topic, envelope, cancellationToken);
    }

    protected override Task PublishToBrokerAsync(string topic, string pubSubName, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
    {
        // Deserialize the envelope to object so Dapr can serialize it properly
        // This prevents double-serialization (string wrapping) by Dapr
        var envelope = EventSerializer.Deserialize<object>(serializedEnvelope);
        
        // Publish as object - Dapr will serialize it as JSON object (not string-wrapped)
        return daprClient.PublishEventAsync(pubSubName, topic, envelope, cancellationToken);
    }
}
