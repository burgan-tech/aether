using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes <see cref="OutboxWakeupEvent"/> straight to the configured pub/sub component,
/// bypassing the outbox by design (the nudge must not create the work it announces).
/// </summary>
public sealed class DaprOutboxWakeupNotifier(
    DaprClient daprClient,
    ITopicNameStrategy topicNameStrategy,
    AetherEventBusOptions eventBusOptions) : IOutboxWakeupNotifier
{
    private readonly string _topic = topicNameStrategy.GetTopicName(typeof(OutboxWakeupEvent));

    public Task NotifyAsync(CancellationToken cancellationToken = default)
        => daprClient.PublishEventAsync(
            eventBusOptions.PubSubName,
            _topic,
            new OutboxWakeupEvent(),
            cancellationToken);
}
