using System;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Events;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Represents an outbox message for the transactional outbox pattern.
/// Used to store events that failed to publish or need to be retried.
/// </summary>
public class OutboxMessage : Entity<Guid>, IHasExtraProperties, IHasCreatedAt
{
    private OutboxMessage()
    {
    }

    public OutboxMessage(Guid id, string eventName, byte[] eventData) : base(id)
    {
        EventName = eventName;
        EventData = eventData;
        ExtraProperties = new ExtraPropertyDictionary();
        Status = OutboxMessageStatus.Pending;
    }

    /// <summary>
    /// Gets or sets the event name.
    /// </summary>
    public string EventName { get; private set; } = default!;

    /// <summary>
    /// Gets or sets the serialized event data (CloudEventEnvelope).
    /// </summary>
    public byte[] EventData { get; private set; } = default!;

    /// <summary>
    /// Gets or sets the processing status.
    /// </summary>
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

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
    /// Gets or sets the worker ID that currently holds the lock on this message.
    /// </summary>
    public string? LockedBy { get; set; }

    /// <summary>
    /// Gets or sets when the lock expires.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Gets or sets extra properties for storing metadata (pubSubName, version, topicName, etc.).
    /// </summary>
    public ExtraPropertyDictionary ExtraProperties { get; private set; } = new ExtraPropertyDictionary();
}

