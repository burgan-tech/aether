namespace BBT.Aether.Domain.Events;

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
    /// Event has been successfully processed.
    /// </summary>
    Processed = 1,

    /// <summary>
    /// Event has been discarded (e.g., exceeded max retry count).
    /// </summary>
    Discarded = 2
}

