using System;

namespace BBT.Aether.Events;

/// <summary>
/// Describes an event subscription with event name, version, topic name, CLR type, and PubSub component.
/// </summary>
public sealed record EventSubscriptionDescriptor(
    string TopicName,
    Type ClrEventType,
    string PubSubName);
