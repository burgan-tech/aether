using System;

namespace BBT.Aether.Events;

/// <summary>Configuration options for the outbox pattern.</summary>
public class AetherOutboxOptions
{
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
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

    // ── Partitioning ──────────────────────────────────────────────────────────

    /// <summary>
    /// When true, each worker acquires up to <see cref="MaxOwnedPartitions"/> partition leases from the
    /// <c>partition_leases</c> table and only processes messages in owned partitions.
    /// When false (default) all messages are processed by all pods — the previous behaviour.
    /// </summary>
    public bool PartitioningEnabled { get; set; } = false;

    /// <summary>Maximum number of partitions a single pod may own at once.</summary>
    public int MaxOwnedPartitions { get; set; } = 8;

    /// <summary>How long each partition lease is held before it expires.</summary>
    public TimeSpan PartitionLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}
