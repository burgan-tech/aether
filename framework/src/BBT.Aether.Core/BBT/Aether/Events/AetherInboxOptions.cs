using System;

namespace BBT.Aether.Events;

/// <summary>Configuration options for the inbox pattern.</summary>
public class AetherInboxOptions
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Minimum time between cleanups of processed messages on one worker. A cleanup that deletes
    /// a full <see cref="CleanupBatchSize"/> batch leaves the next cycle due, so a backlog keeps
    /// draining one batch per cycle; the interval applies once a batch comes back short.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of processed messages deleted per cleanup cycle.</summary>
    public int CleanupBatchSize { get; set; } = 1000;

    public int ProcessingBatchSize { get; set; } = 100;
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BusyPollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxPollingInterval  { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The database schema whose inbox table this processor handles.
    /// </summary>
    public string? Schema { get; set; }
}
