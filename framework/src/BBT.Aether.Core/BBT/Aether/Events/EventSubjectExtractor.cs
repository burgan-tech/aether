using System;
using System.Linq;
using System.Reflection;

namespace BBT.Aether.Events;

/// <summary>
/// Extracts the CloudEvent subject value from properties marked with [EventSubject] attribute.
/// </summary>
public static class EventSubjectExtractor
{
    /// <summary>
    /// Extracts the subject value from an event instance by finding the first property
    /// decorated with [EventSubject] attribute and returning its string value.
    /// </summary>
    /// <param name="eventInstance">The event instance to extract subject from</param>
    /// <returns>The subject value as a string, or null if no [EventSubject] attribute found or value is null</returns>
    public static string? ExtractSubject(object eventInstance)
    {
        if (eventInstance == null)
        {
            return null;
        }

        var eventType = eventInstance.GetType();
        
        // Find first property with [EventSubject] attribute
        var subjectProperty = eventType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetCustomAttribute<EventSubjectAttribute>() != null);

        if (subjectProperty == null)
        {
            return null;
        }

        // Get property value
        var value = subjectProperty.GetValue(eventInstance);
        
        // Return null if value is null, otherwise convert to string
        return value?.ToString();
    }
}

