using System;

namespace BBT.Aether.Events;

/// <summary>
/// Configuration options for the outbox pattern.
/// </summary>
public class AetherOutboxOptions
{
    /// <summary>
    /// Gets or sets the interval between outbox processing runs.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of retry attempts for failed messages.
    /// Default is 5.
    /// </summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the retention period for processed messages before they are deleted.
    /// Default is 7 days.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the maximum number of messages to process in a single batch.
    /// Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the base delay for exponential backoff retry strategy.
    /// Default is 1 minute.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the lease duration for message processing.
    /// Messages are locked for this duration while being processed.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}

