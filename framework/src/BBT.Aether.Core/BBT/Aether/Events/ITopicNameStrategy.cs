using System;

namespace BBT.Aether.Events;

public interface ITopicNameStrategy
{
    string GetTopicName(Type eventType);
}
