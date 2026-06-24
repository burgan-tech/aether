namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Represents the status of a background job.
/// </summary>
public enum BackgroundJobStatus
{
    /// <summary>
    /// Job has been scheduled but not yet started.
    /// </summary>
    Scheduled = 0,

    /// <summary>
    /// Job is currently running.
    /// </summary>
    Running = 1,

    /// <summary>
    /// Job has completed successfully.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Job has failed during execution.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Job has been cancelled.
    /// </summary>
    Cancelled = 4,

    /// <summary>
    /// Row persisted, not yet armed in the scheduler.
    /// </summary>
    Pending = 5,

    /// <summary>
    /// A failed one-shot attempt awaiting its next retry arming.
    /// </summary>
    Retrying = 6
}
