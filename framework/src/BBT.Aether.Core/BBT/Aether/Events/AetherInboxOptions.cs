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
}

