using System;
using System.Threading;
using System.Threading.Tasks;
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
        // Resolve dependencies from service provider
        var serializer = serviceProvider.GetRequiredService<IEventSerializer>();
        var handler = serviceProvider.GetRequiredService<IEventHandler<T>>();
        
        // Deserialize to strongly-typed CloudEventEnvelope<T>
        var envelope = serializer.Deserialize<CloudEventEnvelope<T>>(body.Span);
        
        if (envelope == null)
        {
            throw new InvalidOperationException($"Failed to deserialize CloudEventEnvelope<{typeof(T).Name}>");
        }
        
        // Invoke handler with strongly-typed envelope
        await handler.HandleAsync(envelope, cancellationToken);
    }
}

