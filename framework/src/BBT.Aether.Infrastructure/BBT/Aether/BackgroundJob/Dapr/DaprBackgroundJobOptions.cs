using System;
using Dapr.Jobs.Models;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Options for configuring a Dapr background job.
/// </summary>
public class DaprBackgroundJobOptions
{
    /// <summary>
    /// Gets or sets the schedule for the job.
    /// </summary>
    public required DaprJobSchedule Schedule { get; set; }

    /// <summary>
    /// Gets or sets the payload for the job.
    /// </summary>
    public ReadOnlyMemory<byte>? JobPayload { get; set; }

    /// <summary>
    /// Gets or sets the starting time for the job.
    /// </summary>
    public DateTimeOffset? StartingFrom { get; set; }

    /// <summary>
    /// Gets or sets the number of times the job should repeat.
    /// </summary>
    public int? Repeats { get; set; }

    /// <summary>
    /// Gets or sets the time-to-live for the job.
    /// </summary>
    public DateTimeOffset? Ttl { get; set; }
}
