using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Represents a precompiled invoker for a specific event handler.
/// Built at startup time to avoid runtime reflection.
/// </summary>
public interface IDistributedEventInvoker
{
    /// <summary>
    /// Event name from [EventName] attribute.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Event version from [EventName] attribute.
    /// </summary>
    int Version { get; }
    
    /// <summary>
    /// Topic name for this event (used in Dapr subscriptions).
    /// </summary>
    string Topic { get; }
    
    /// <summary>
    /// PubSub component name for this event.
    /// </summary>
    string PubSubName { get; }
    
    /// <summary>
    /// Invokes the event handler with the provided event data.
    /// Deserializes the payload and calls the strongly-typed handler.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving handler and dependencies</param>
    /// <param name="body">Serialized CloudEventEnvelope bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InvokeAsync(IServiceProvider serviceProvider, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

