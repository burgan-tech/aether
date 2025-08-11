using System.Collections.Concurrent;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Represents a list of job handlers for the Dapr job scheduler.
/// </summary>
public class DaprJobSchedulerHandlerList
{
    /// <summary>
    /// Gets the dictionary of job handlers.
    /// The value is the handler type.
    /// </summary>
    public ConcurrentBag<JobHandlerInfo> JobHandlers { get; } = new();
}
