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
        var eventNameAttr = eventType.GetCustomAttributes(typeof(EventNameAttribute), false)
            .FirstOrDefault() as EventNameAttribute;

        if (eventNameAttr != null)
        {
            // Format: EventName.vVersion
            topicName = $"{eventNameAttr.Name}.v{eventNameAttr.Version}";
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