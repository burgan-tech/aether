namespace BBT.Aether.Events;

/// <summary>
/// Status of an outgoing event in the outbox.
/// </summary>
public enum OutboxMessageStatus
{
    /// <summary>
    /// Event is pending processing or retry.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Event is currently being processed.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Event has been successfully processed.
    /// </summary>
    Processed = 2
}

