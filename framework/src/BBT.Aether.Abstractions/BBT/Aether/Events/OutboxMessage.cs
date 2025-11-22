using System;
using System.Collections.Generic;

namespace BBT.Aether.Events;

/// <summary>
/// Represents an outbox message for the transactional outbox pattern.
/// Used to store events that failed to publish or need to be retried.
/// </summary>
public class OutboxMessage
{
    /// <summary>
    /// Gets or sets the unique identifier for the message.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the event name.
    /// </summary>
    public string EventName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the serialized event data (CloudEventEnvelope).
    /// </summary>
    public byte[] EventData { get; set; } = default!;

    /// <summary>
    /// Gets or sets when the message was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the message was successfully processed (null if not processed).
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Gets or sets the number of times processing has been attempted.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the last error message (if any).
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the next retry time (null if no retry scheduled).
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// Gets or sets extra properties for storing metadata (pubSubName, version, topicName, etc.).
    /// </summary>
    public Dictionary<string, object> ExtraProperties { get; set; } = new();
}

