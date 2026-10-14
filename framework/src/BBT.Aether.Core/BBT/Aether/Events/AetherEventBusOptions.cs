namespace BBT.Aether.Events;

/// <summary>
/// Configuration options for Aether Event Bus.
/// </summary>
public class AetherEventBusOptions
{
    /// <summary>
    /// Gets or sets whether to prefix environment name to topic names.
    /// Default: true
    /// </summary>
    public bool PrefixEnvironmentToTopic { get; set; } = true;

    /// <summary>
    /// Gets or sets a fixed prefix for every topic name, e.g. <c>"vnext.core.production"</c> gives
    /// <c>vnext.core.production.{eventName}.v{version}</c>. When set it replaces the environment prefix
    /// (<see cref="PrefixEnvironmentToTopic"/> is ignored) and also applies to a topic given explicitly
    /// through <see cref="EventNameAttribute"/>, so broker ACLs can be granted on the prefix alone.
    /// A trailing dot is optional. Default: null (no fixed prefix).
    /// </summary>
    public string? TopicPrefix { get; set; }

    /// <summary>
    /// Gets or sets the default Source value for CloudEventEnvelope.
    /// Format: "urn:vnext:{service}" (e.g., "urn:vnext:order-service")
    /// If not set, Source must be provided manually when creating CloudEventEnvelope.
    /// </summary>
    public string DefaultSource { get; set; } = default!;

    /// <summary>
    /// Gets or sets the default name of the Dapr PubSub component.
    /// This can be overridden per event using the PubSubName parameter in EventNameAttribute.
    /// Default: "pubsub"
    /// </summary>
    public string PubSubName { get; set; } = "pubsub";
}
