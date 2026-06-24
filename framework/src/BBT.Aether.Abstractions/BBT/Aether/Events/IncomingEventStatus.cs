namespace BBT.Aether.Events;

/// <summary>
/// Status of an incoming event in the inbox.
/// </summary>
public enum IncomingEventStatus
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
    Processed = 2,

    /// <summary>
    /// Intentionally discarded — no handler found or deserialization failed; no retry performed.
    /// </summary>
    Discarded = 3,

    /// <summary>
    /// Maximum retry count exceeded; dead letter. Manual intervention required.
    /// </summary>
    DeadLetter = 4
}

