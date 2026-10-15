using System;
using System.Linq;
using Microsoft.Extensions.Hosting;

namespace BBT.Aether.Events;

/// <summary>
/// Builds the publish-side topic name through <see cref="TopicNameFormatter"/>.
/// Takes the <see cref="AetherEventBusOptions"/> instance <c>AddAetherEventBus</c> registers — the same
/// one the subscriber side reads. It must not read <c>IOptions&lt;AetherEventBusOptions&gt;</c>: nothing
/// configures that, so it would always see the defaults and ignore <see cref="AetherEventBusOptions.TopicPrefix"/>.
/// </summary>
public class DefaultTopicNameStrategy(
    IHostEnvironment environment,
    AetherEventBusOptions options)
    : ITopicNameStrategy
{
    private readonly AetherEventBusOptions _options = options;

    public string GetTopicName(Type eventType)
    {
        if (eventType.GetCustomAttributes(typeof(EventNameAttribute), false)
                .FirstOrDefault() is EventNameAttribute eventNameAttr)
        {
            return TopicNameFormatter.Format(
                eventNameAttr.Name,
                eventNameAttr.Version,
                eventNameAttr.Topic,
                _options,
                environment.EnvironmentName);
        }

        // Fallback to Type.FullName if EventNameAttribute is missing
        return TopicNameFormatter.Format(
            eventName: string.Empty,
            version: 0,
            topicOverride: eventType.FullName!,
            _options,
            environment.EnvironmentName);
    }
}