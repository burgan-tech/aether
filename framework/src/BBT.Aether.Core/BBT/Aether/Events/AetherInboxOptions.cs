using System;

namespace BBT.Aether.Events;

/// <summary>
/// Configuration options for the inbox pattern.
/// </summary>
public class AetherInboxOptions
{
    /// <summary>
    /// Gets or sets the retention period for processed inbox messages before they are deleted.
    /// Default is 7 days.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the interval between inbox cleanup runs.
    /// Default is 1 hour.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the maximum number of messages to delete in a single cleanup batch.
    /// Default is 1000.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the interval between inbox processing runs for pending events.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of pending events to process in a single batch.
    /// Default is 100.
    /// </summary>
    public int ProcessingBatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the distributed lock name for inbox processing coordination.
    /// Default is "Aether_InboxProcessor".
    /// </summary>
    public string DistributedLockName { get; set; } = "Aether_InboxProcessor";

    /// <summary>
    /// Gets or sets the lock expiry time in seconds for distributed lock.
    /// Default is 60 seconds.
    /// </summary>
    public int LockExpirySeconds { get; set; } = 60;
}

