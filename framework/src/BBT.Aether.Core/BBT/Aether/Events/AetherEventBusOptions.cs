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
