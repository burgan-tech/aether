using System.Collections.Generic;

namespace BBT.Aether.Events;

/// <summary>
/// Represents a Dapr subscription response following Dapr subscription API format.
/// </summary>
public class DaprSubscription
{
    /// <summary>
    /// Gets or sets the topic name.
    /// </summary>
    public string Topic { get; set; } = default!;

    /// <summary>
    /// Gets or sets the pubsub component name.
    /// </summary>
    public string PubsubName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the route path (V1 format, for backward compatibility).
    /// </summary>
    public string? Route { get; set; }

    /// <summary>
    /// Gets or sets the routes structure (V2 format, for advanced routing rules).
    /// </summary>
    public DaprRoutes? Routes { get; set; }

    /// <summary>
    /// Gets or sets the metadata dictionary.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Gets or sets the dead letter topic.
    /// </summary>
    public string? DeadLetterTopic { get; set; }
}

