using System;

namespace BBT.Aether.Events;

/// <summary>
/// Contains metadata about a distributed event extracted from EventNameAttribute.
/// This metadata is used to publish events without requiring reflection at dispatch time.
/// </summary>
public sealed class EventMetadata(
    Type eventType,
    string eventName,
    int version,
    string? pubSubName = null)
{
    /// <summary>
    /// Gets the event type (CLR type).
    /// </summary>
    public Type EventType { get; } = eventType;

    /// <summary>
    /// Gets the event name from EventNameAttribute.
    /// </summary>
    public string EventName { get; } = eventName;

    /// <summary>
    /// Gets the event version.
    /// </summary>
    public int Version { get; } = version;

    /// <summary>
    /// Gets the PubSub component name (null if default should be used).
    /// </summary>
    public string? PubSubName { get; } = pubSubName;
}

