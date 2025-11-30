using System;
using System.Linq;

namespace BBT.Aether.Events;

/// <summary>
/// Contains event name information extracted from EventNameAttribute.
/// </summary>
/// <param name="EventName">The event name</param>
/// <param name="Version">The event version</param>
/// <param name="PubSubName">The PubSub component name (null if default should be used)</param>
public sealed record EventNameInfo(string EventName, int Version, string? PubSubName);

/// <summary>
/// Specifies the event name and version for a distributed event.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EventNameAttribute(string name, int version = 1, string? pubSubName = null, string? topic = null, string? dataSchema = null) : Attribute
{
    public string Name { get; } = name;
    public int Version { get; } = version;
    
    /// <summary>
    /// Gets the name of the Dapr PubSub component to use for this event.
    /// If null, the default PubSubName from AetherEventBusOptions will be used.
    /// </summary>
    public string? PubSubName { get; } = pubSubName;
    
    /// <summary>
    /// Gets the optional topic override for this event.
    /// If null, topic will be auto-generated from name and version.
    /// </summary>
    public string? Topic { get; } = topic;
    
    /// <summary>
    /// Gets the optional data schema URI for this event.
    /// </summary>
    public string? DataSchema { get; } = dataSchema;

    /// <summary>
    /// Gets the event name information from an event type.
    /// Returns EventNameInfo containing Name, Version, and PubSubName from the EventNameAttribute if present,
    /// otherwise returns default values (type full name, version 1, null pubSubName).
    /// </summary>
    /// <param name="eventType">The event type to extract information from</param>
    /// <returns>EventNameInfo containing the event metadata</returns>
    public static EventNameInfo GetEventNameInfo(Type eventType)
    {
        Check.NotNull(eventType, nameof(eventType));

        if (eventType
                .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
                .FirstOrDefault() is EventNameAttribute attribute)
        {
            return new EventNameInfo(attribute.Name, attribute.Version, attribute.PubSubName);
        }

        // Default: use full type name, version 1, no specific pubSubName
        return new EventNameInfo(eventType.FullName ?? eventType.Name, 1, null);
    }
}
