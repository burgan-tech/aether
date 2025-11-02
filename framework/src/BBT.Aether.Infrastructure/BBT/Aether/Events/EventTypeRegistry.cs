using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace BBT.Aether.Events;

/// <summary>
/// Implementation of IEventTypeRegistry for resolving event types.
/// </summary>
public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _map = new();
    private readonly List<EventSubscriptionDescriptor> _descriptors = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EventTypeRegistry"/> class.
    /// </summary>
    /// <param name="descriptors">The event subscription descriptors</param>
    public EventTypeRegistry(IEnumerable<EventSubscriptionDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            _map[descriptor.TopicName] = descriptor.ClrEventType;
            _descriptors.Add(descriptor);
        }
        All = _descriptors.AsReadOnly();
    }

    /// <inheritdoc />
    public Type? Resolve(string topicName)
    {
        return _map.GetValueOrDefault(topicName);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<EventSubscriptionDescriptor> All { get; }
}
