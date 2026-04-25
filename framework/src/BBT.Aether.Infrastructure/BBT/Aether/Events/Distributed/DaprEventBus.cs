using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Telemetry;
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

    protected override async Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
    {
        var pubSubName = ResolvePubSubName<TEvent>();

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "EventBus.PublishToBroker",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("event.topic", topic);
        activity?.SetTag("event.pubsub_name", pubSubName);
        activity?.SetTag("event.broker", "dapr");

        try
        {
            var envelope = EventSerializer.Deserialize<object>(serializedEnvelope);
            await daprClient.PublishEventAsync(pubSubName, topic, envelope, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    protected override async Task PublishToBrokerAsync(string topic, string pubSubName, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "EventBus.PublishToBroker",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("event.topic", topic);
        activity?.SetTag("event.pubsub_name", pubSubName);
        activity?.SetTag("event.broker", "dapr");

        try
        {
            var envelope = EventSerializer.Deserialize<object>(serializedEnvelope);
            await daprClient.PublishEventAsync(pubSubName, topic, envelope, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }
}
