namespace BBT.Aether.Events;

/// <summary>
/// The single place a topic name is built. The publisher (<see cref="DefaultTopicNameStrategy"/>) and the
/// subscriber (<see cref="EventHandlerAutoDiscovery"/>) both go through it, so the two sides cannot drift
/// apart: a mismatch would publish to one topic while the handler listens on another, losing every event
/// silently.
/// </summary>
public static class TopicNameFormatter
{
    /// <summary>
    /// Builds the broker topic for an event.
    /// </summary>
    /// <param name="eventName">The event name from <see cref="EventNameAttribute"/>.</param>
    /// <param name="version">The event version from <see cref="EventNameAttribute"/>.</param>
    /// <param name="topicOverride">An explicit topic from <see cref="EventNameAttribute"/>, if any.</param>
    /// <param name="options">The event bus options.</param>
    /// <param name="environmentName">The host environment name, used only by the legacy environment prefix.</param>
    /// <returns>
    /// <c>{TopicPrefix}.{topic}</c> when <see cref="AetherEventBusOptions.TopicPrefix"/> is set;
    /// otherwise <c>{environment}.{topic}</c> when <see cref="AetherEventBusOptions.PrefixEnvironmentToTopic"/>
    /// is on; otherwise <c>{topic}</c>. <c>{topic}</c> is the override, or <c>{eventName}.v{version}</c>.
    /// </returns>
    public static string Format(
        string eventName,
        int version,
        string? topicOverride,
        AetherEventBusOptions options,
        string? environmentName)
    {
        var topicName = !string.IsNullOrWhiteSpace(topicOverride)
            ? topicOverride
            : $"{eventName}.v{version}";

        if (!string.IsNullOrWhiteSpace(options.TopicPrefix))
        {
            return $"{options.TopicPrefix.TrimEnd('.')}.{topicName}";
        }

        if (options.PrefixEnvironmentToTopic && !string.IsNullOrWhiteSpace(environmentName))
        {
            return $"{environmentName.ToLowerInvariant()}.{topicName}";
        }

        return topicName;
    }
}
