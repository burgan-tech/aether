using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Events;

public class DaprEventBus(
    DaprClient daprClient,
    AetherEventBusOptions options,
    ITopicNameStrategy topicNameStrategy,
    IEventSerializer eventSerializer,
    IServiceScopeFactory serviceScopeFactory)
    : DistributedEventBusBase(topicNameStrategy, eventSerializer, serviceScopeFactory, options)
{
    private readonly AetherEventBusOptions _options = options;

    /// <summary>
    /// Resolves the PubSub component name for an event type.
    /// Checks for EventNameAttribute on event type, falls back to default from options.
    /// </summary>
    private string ResolvePubSubName<TEvent>() where TEvent : class
    {
        // Check for EventNameAttribute on event type
        var attribute = typeof(TEvent)
            .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
            .FirstOrDefault() as EventNameAttribute;

        if (attribute != null && !string.IsNullOrWhiteSpace(attribute.PubSubName))
        {
            return attribute.PubSubName;
        }

        // Fall back to default from options
        return _options.PubSubName;
    }

    protected override Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
    {
        // Resolve PubSub component name from event type attribute or default
        var pubSubName = ResolvePubSubName<TEvent>();

        // Deserialize the envelope to object so Dapr can serialize it properly
        // This prevents double-serialization (string wrapping) by Dapr
        var envelope = System.Text.Json.JsonSerializer.Deserialize<object>(serializedEnvelope);
        
        // Publish as object - Dapr will serialize it as JSON object (not string-wrapped)
        return daprClient.PublishEventAsync(pubSubName, topic, envelope, cancellationToken);
    }
}
