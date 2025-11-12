using System.Linq;

namespace BBT.Aether.Events;

/// <summary>
/// Static generic cache for event metadata extracted from [EventName] attribute.
/// Metadata is read once per event type T at static initialization time.
/// Zero runtime reflection after first access to EventMeta&lt;T&gt;.
/// </summary>
/// <typeparam name="T">The event type</typeparam>
public static class EventMeta<T>
{
    /// <summary>
    /// Event name from [EventName] attribute.
    /// </summary>
    public readonly static string Name;
    
    /// <summary>
    /// Event version from [EventName] attribute.
    /// </summary>
    public readonly static int Version;
    
    /// <summary>
    /// PubSub component name from [EventName] attribute (null if default should be used).
    /// </summary>
    public readonly static string? PubSub;
    
    /// <summary>
    /// Topic override from [EventName] attribute (null if should be auto-generated).
    /// </summary>
    public readonly static string? Topic;
    
    /// <summary>
    /// Data schema URI from [EventName] attribute.
    /// </summary>
    public readonly static string? DataSchema;

    /// <summary>
    /// Static constructor - extracts metadata once per event type.
    /// </summary>
    static EventMeta()
    {
        var eventType = typeof(T);
        
        var attribute = eventType
            .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
            .FirstOrDefault() as EventNameAttribute;

        if (attribute == null)
        {
            // Default values if no attribute present
            Name = eventType.FullName ?? eventType.Name;
            Version = 1;
            PubSub = null;
            Topic = null;
            DataSchema = null;
        }
        else
        {
            Name = attribute.Name;
            Version = attribute.Version;
            PubSub = attribute.PubSubName;
            Topic = attribute.Topic;
            DataSchema = attribute.DataSchema;
        }
    }
}

