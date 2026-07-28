using System;

namespace BBT.Aether.Events;

/// <summary>Configuration options for the outbox pattern.</summary>
public class AetherOutboxOptions
{
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Minimum time between retention cleanup passes. Cleanup runs at most once per interval,
    /// not on every dispatcher poll.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of processed messages deleted per cleanup pass.</summary>
    public int CleanupBatchSize { get; set; } = 1000;

    public int BatchSize { get; set; } = 100;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BusyPollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxPollingInterval  { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The database schema whose outbox table this processor handles.
    /// </summary>
    public string? Schema { get; set; } = "sys_queues";

    /// <summary>
    /// Number of logical partitions messages are hashed into.
    /// <para>
    /// This is NOT a runtime knob. Changing it re-maps every key and requires a migration
    /// plan; existing rows keep their old partition. Algorithm and version are recorded on
    /// <see cref="MessagePartitionResolver"/>.
    /// </para>
    /// </summary>
    public int PartitionCount { get; set; } = 64;
}
