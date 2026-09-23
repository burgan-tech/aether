using System;

namespace BBT.Aether.Events;

/// <summary>Configuration options for the outbox pattern.</summary>
public class AetherOutboxOptions
{
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Minimum time between cleanups of processed messages on one worker. A cleanup that deletes
    /// a full <see cref="CleanupBatchSize"/> batch leaves the next cycle due, so a backlog keeps
    /// draining one batch per cycle; the interval applies once a batch comes back short.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of processed messages deleted per cleanup cycle.</summary>
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
    /// When true, a unit of work that stored outbox messages publishes a direct pub/sub wake nudge
    /// (<see cref="OutboxWakeupEvent"/>) after commit so outbox processors poll immediately instead
    /// of waiting out the idle interval. Default false. Requires an IOutboxWakeupNotifier registration.
    /// </summary>
    public bool WakeupSignalEnabled { get; set; }
}
