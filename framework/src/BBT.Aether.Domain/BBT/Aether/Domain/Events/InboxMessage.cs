using System;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Events;

namespace BBT.Aether.Domain.Events;

/// <summary>
/// Represents an inbox message for idempotency checking.
/// Used to track processed events and prevent duplicate processing.
/// </summary>
public class InboxMessage : Entity<string>, IHasExtraProperties, IHasCreatedAt
{
    private InboxMessage()
    {
    }

    public InboxMessage(string id, string eventName, byte[] eventData) : base(id)
    {
        EventName = eventName;
        EventData = eventData;

        ExtraProperties = new ExtraPropertyDictionary();
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
    public ExtraPropertyDictionary ExtraProperties { get; private set; } = new ExtraPropertyDictionary();

    /// <summary>
    /// Marks the message as currently being processed.
    /// </summary>
    public void MarkAsProcessing()
    {
        Status = IncomingEventStatus.Processing;
    }

    /// <summary>
    /// Marks the message as processed.
    /// </summary>
    /// <param name="processedTime">The time when processing completed</param>
    public void MarkAsProcessed(DateTime processedTime)
    {
        Status = IncomingEventStatus.Processed;
        HandledTime = processedTime;
    }

    /// <summary>
    /// Marks the message as discarded.
    /// </summary>
    /// <param name="discardedTime">The time when the message was discarded</param>
    public void MarkAsDiscarded(DateTime discardedTime)
    {
        Status = IncomingEventStatus.Discarded;
        HandledTime = discardedTime;
    }

    /// <summary>
    /// Schedules a retry for the message.
    /// </summary>
    /// <param name="retryCount">The new retry count</param>
    /// <param name="nextRetryTime">The next scheduled retry time</param>
    public void RetryLater(int retryCount, DateTime nextRetryTime)
    {
        Status = IncomingEventStatus.Pending;
        NextRetryTime = nextRetryTime;
        RetryCount = retryCount;
    }
}

