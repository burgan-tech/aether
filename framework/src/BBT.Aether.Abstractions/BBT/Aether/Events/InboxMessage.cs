using System;
using System.Collections.Generic;

namespace BBT.Aether.Events;

/// <summary>
/// Represents an inbox message for idempotency checking.
/// Used to track processed events and prevent duplicate processing.
/// </summary>
public class InboxMessage
{
    /// <summary>
    /// Gets or sets the unique identifier for the message (event ID).
    /// </summary>
    public string Id { get; set; } = default!;

    /// <summary>
    /// Gets or sets the event name.
    /// </summary>
    public string EventName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the serialized event data (CloudEventEnvelope).
    /// </summary>
    public byte[] EventData { get; set; } = default!;

    /// <summary>
    /// Gets or sets when the message was created/received.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the processing status.
    /// </summary>
    public IncomingEventStatus Status { get; set; } = IncomingEventStatus.Pending;

    /// <summary>
    /// Gets or sets when the message was handled.
    /// </summary>
    public DateTime? HandledTime { get; set; }

    /// <summary>
    /// Gets or sets the number of retry attempts.
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// Gets or sets the next retry time.
    /// </summary>
    public DateTime? NextRetryTime { get; set; } = null;

    /// <summary>
    /// Gets or sets extra properties for storing metadata (pubSubName, version, etc.).
    /// </summary>
    public Dictionary<string, object> ExtraProperties { get; set; } = new();
}

