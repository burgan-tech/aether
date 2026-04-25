using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Events;

/// <summary>
/// Precompiled invoker for a specific event type handler.
/// Uses EventMeta&lt;T&gt; for metadata (no runtime reflection).
/// </summary>
/// <typeparam name="T">The event data type</typeparam>
public sealed class DistributedEventInvoker<T> : IDistributedEventInvoker
{
    /// <inheritdoc />
    public string Name => EventMeta<T>.Name;
    
    /// <inheritdoc />
    public int Version => EventMeta<T>.Version;
    
    /// <inheritdoc />
    public string Topic { get; }
    
    /// <inheritdoc />
    public string PubSubName { get; }

    /// <summary>
    /// Creates a new invoker for event type T.
    /// </summary>
    /// <param name="topic">The computed topic name for Dapr subscription</param>
    /// <param name="pubSubName">The PubSub component name</param>
    public DistributedEventInvoker(string topic, string pubSubName)
    {
        Topic = topic;
        PubSubName = pubSubName;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IServiceProvider serviceProvider, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "Inbox.Invoke",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);

        activity?.SetTag("event.name", Name);
        activity?.SetTag("event.version", Version);

        try
        {
            var serializer = serviceProvider.GetRequiredService<IEventSerializer>();
            var handler = serviceProvider.GetRequiredService<IEventHandler<T>>();
            
            activity?.SetTag("event.handler", handler.GetType().Name);

            var envelope = serializer.Deserialize<CloudEventEnvelope<T>>(body.Span);
            
            if (envelope == null)
            {
                throw new InvalidOperationException($"Failed to deserialize CloudEventEnvelope<{typeof(T).Name}>");
            }
            
            await handler.HandleAsync(envelope, cancellationToken);
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
