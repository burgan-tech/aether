using System;
using System.Linq;

namespace BBT.Aether.Events;

/// <summary>
/// Extracts metadata from EventNameAttribute on distributed events.
/// </summary>
public static class EventMetadataExtractor
{
    /// <summary>
    /// Extracts event metadata from the EventNameAttribute on the event type.
    /// </summary>
    /// <param name="event">The distributed event</param>
    /// <returns>Event metadata</returns>
    /// <exception cref="InvalidOperationException">Thrown if EventNameAttribute is missing</exception>
    public static EventMetadata Extract(IDistributedEvent @event)
    {
        var eventType = @event.GetType();
        
        var attribute = eventType
            .GetCustomAttributes(typeof(EventNameAttribute), inherit: false)
            .FirstOrDefault() as EventNameAttribute;

        if (attribute == null)
        {
            throw new InvalidOperationException(
                $"Event type '{eventType.FullName}' must have [EventName] attribute. " +
                $"Example: [EventName(\"OrderCreated\", version: 1)]");
        }

        return new EventMetadata(
            eventType: eventType,
            eventName: attribute.Name,
            version: attribute.Version,
            pubSubName: attribute.PubSubName,
            topic: attribute.Topic,
            dataSchema: attribute.DataSchema
        );
    }
}

