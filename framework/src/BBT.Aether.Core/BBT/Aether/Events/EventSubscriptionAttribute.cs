using System;

namespace BBT.Aether.Events;

/// <summary>
/// Specifies subscription metadata for an event handler.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class EventSubscriptionAttribute(string eventName, int version = 1, string? pubSubName = null) : Attribute
{
    public string EventName { get; } = eventName;
    public int Version { get; } = version;
    
    /// <summary>
    /// Gets the name of the Dapr PubSub component to use for this subscription.
    /// If null, the default PubSubName from AetherEventBusOptions will be used.
    /// </summary>
    public string? PubSubName { get; } = pubSubName;
}
