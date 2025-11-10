using System;

namespace BBT.Aether.Events;

/// <summary>
/// CloudEvents 1.0 compliant envelope for distributed events.
/// This envelope is automatically created by the EventBus when publishing events.
/// The Type is generated from EventNameAttribute on the event type using TopicNameStrategy.
/// The Source is populated from AetherEventBusOptions.DefaultSource.
/// </summary>
public class CloudEventEnvelope
{
    /// <summary>
    /// CloudEvents specification version. Default: "1.0"
    /// </summary>
    public string SpecVersion { get; init; } = "1.0";

    /// <summary>
    /// Unique identifier for the event. Auto-generated as GUID if not provided.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Event type identifier. Automatically generated from EventNameAttribute on the event type using TopicNameStrategy.
    /// Format: "domain.EventName.vX" or "{environment}.domain.EventName.vX" if environment prefix is enabled.
    /// </summary>
    public string Type { get; init; } = default!;

    /// <summary>
    /// Source identifier. Automatically populated from AetherEventBusOptions.DefaultSource.
    /// Format: "urn:vnext:{service}"
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Subject identifier (e.g., aggregate ID). Optional, provided via PublishAsync subject parameter.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// Event timestamp. Default: DateTimeOffset.UtcNow
    /// </summary>
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Content type of the data. Default: "application/json"
    /// </summary>
    public string? DataContentType { get; init; } = "application/json";
    
    /// <summary>
    /// URI reference to the schema that the data adheres to.
    /// </summary>
    public string? DataSchema { get; init; }

    /// <summary>
    /// The event payload data.
    /// </summary>
    public object Data { get; init; } = default!;
}

/// <summary>
/// Generic CloudEvents 1.0 compliant envelope for distributed events with strongly-typed data.
/// Used by event handlers for type-safe access to event data and metadata.
/// </summary>
/// <typeparam name="TData">The type of the event data</typeparam>
public class CloudEventEnvelope<TData> : CloudEventEnvelope
{
    /// <summary>
    /// The strongly-typed event payload data.
    /// </summary>
    public new TData Data { get; init; } = default!;
}
