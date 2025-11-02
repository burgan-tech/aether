using System;
using System.Collections.Generic;

namespace BBT.Aether.Events;

/// <summary>
/// Registry for resolving CLR event types from topic name.
/// </summary>
public interface IEventTypeRegistry
{
    /// <summary>
    /// Resolves a CLR event type from CloudEvent Type field (topic name with environment prefix).
    /// </summary>
    /// <param name="topicName">The topic name from CloudEvent Type field (e.g., "development.issue.created.v1")</param>
    /// <returns>The CLR type, or null if not found</returns>
    Type? Resolve(string topicName);

    /// <summary>
    /// Gets all registered event subscription descriptors.
    /// </summary>
    IReadOnlyCollection<EventSubscriptionDescriptor> All { get; }
}
