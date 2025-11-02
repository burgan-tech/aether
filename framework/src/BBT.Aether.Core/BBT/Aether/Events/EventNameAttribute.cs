using System;

namespace BBT.Aether.Events;

/// <summary>
/// Specifies the event name and version for a distributed event.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EventNameAttribute(string name, int version = 1, string? pubSubName = null) : Attribute
{
    public string Name { get; } = name;
    public int Version { get; } = version;
    
    /// <summary>
    /// Gets the name of the Dapr PubSub component to use for this event.
    /// If null, the default PubSubName from AetherEventBusOptions will be used.
    /// </summary>
    public string? PubSubName { get; } = pubSubName;
}
