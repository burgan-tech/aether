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
    /// Event has been discarded (e.g., exceeded max retry count).
    /// </summary>
    Discarded = 3
}

