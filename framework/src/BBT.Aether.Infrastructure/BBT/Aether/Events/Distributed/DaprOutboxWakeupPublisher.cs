using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes outbox wake-up signals over Dapr pub/sub, best-effort.
/// </summary>
/// <remarks>
/// <para>
/// Signals bypass the outbox deliberately: routing a wake-up hint through the very table it is
/// meant to drain would be circular.
/// </para>
/// <para>
/// A failure here is logged and swallowed. The caller is a post-commit hook — the business
/// transaction has already succeeded, and fallback polling still finds the rows, so a broker
/// problem costs latency rather than data.
/// </para>
/// <para>
/// Unlike <see cref="DaprEventBus"/>, this publisher does not go through
/// <c>ITopicNameStrategy</c> or the <c>PrefixEnvironmentToTopic</c> behaviour applied to business
/// events. A wake-up signal is infrastructure chatter between an application and its own
/// dispatcher, not a domain event — the subscribing worker's Dapr subscription YAML names the raw
/// <see cref="AetherOutboxOptions.SignalTopic"/> value directly, so no per-event topic resolution
/// applies here.
/// </para>
/// </remarks>
public sealed class DaprOutboxWakeupPublisher(
    DaprClient daprClient,
    AetherEventBusOptions eventBusOptions,
    AetherOutboxOptions outboxOptions,
    ILogger<DaprOutboxWakeupPublisher> logger) : IOutboxWakeupPublisher
{
    /// <inheritdoc />
    public async Task<bool> TryPublishAsync(
        OutboxWakeupSignal signal,
        CancellationToken cancellationToken = default)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "Outbox.Signal.Publish", ActivityKind.Producer, Activity.Current?.Context ?? default);

        activity?.SetTag("outbox.schema", signal.Schema);
        activity?.SetTag("outbox.partition_id", signal.PartitionId);

        try
        {
            var metadata = new Dictionary<string, string>
            {
                ["ttlInSeconds"] = outboxOptions.SignalTtlSeconds.ToString()
            };

            await daprClient.PublishEventAsync(
                eventBusOptions.PubSubName,
                outboxOptions.SignalTopic,
                signal,
                metadata,
                cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordException(activity, exception);
            logger.LogWarning(
                exception,
                "Outbox wake-up signal could not be published. Schema: {Schema}, PartitionId: {PartitionId}. "
                + "Fallback polling will pick the rows up.",
                signal.Schema, signal.PartitionId);
            return false;
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
