using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Non-generic bridge interface for job execution.
/// Acts as an adapter between external job schedulers (like Dapr) and the internal JobDispatcher.
/// Looks up job entity and routes to the appropriate handler based on handler name.
/// </summary>
public interface IJobExecutionBridge
{
    /// <summary>
    /// Executes a job by looking up the job entity and dispatching to the appropriate handler.
    /// </summary>
    /// <param name="jobName">The unique job name from the external scheduler (e.g., "send-email-order-123")</param>
    /// <param name="payload">Serialized job payload containing handler arguments</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

