using System;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BBT.Aether.Events;

public class DefaultTopicNameStrategy(
    IHostEnvironment environment,
    IOptions<AetherEventBusOptions> options)
    : ITopicNameStrategy
{
    private readonly AetherEventBusOptions _options = options.Value;

    public string GetTopicName(Type eventType)
    {
        string topicName;

        // Try to get eventName and version from EventNameAttribute

        if (eventType.GetCustomAttributes(typeof(EventNameAttribute), false)
                .FirstOrDefault() is EventNameAttribute eventNameAttr)
        {
            topicName = !eventNameAttr.Topic.IsNullOrWhiteSpace() 
                ? eventNameAttr.Topic 
                : $"{eventNameAttr.Name}.v{eventNameAttr.Version}";
        }
        else
        {
            // Fallback to Type.FullName if EventNameAttribute is missing
            topicName = eventType.FullName!;
        }

        // Apply environment prefix if enabled
        if (_options.PrefixEnvironmentToTopic && !string.IsNullOrWhiteSpace(environment.EnvironmentName))
        {
            return $"{environment.EnvironmentName.ToLowerInvariant()}.{topicName}";
        }

        return topicName;
    }
}