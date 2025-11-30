using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Defines the contract for dispatching background jobs to their appropriate handlers.
/// This interface acts as a mediator between the job scheduling system and the specific job handlers.
/// </summary>
public interface IJobDispatcher
{
    /// <summary>
    /// Dispatches a background job to the appropriate handler based on the handler name.
    /// Updates job status before and after execution.
    /// </summary>
    /// <param name="jobId">The unique entity identifier of the job being dispatched.</param>
    /// <param name="handlerName">The name of the handler that should process this job (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="jobPayload">The serialized job payload data to be processed by the handler.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests during job processing.</param>
    /// <returns>A task representing the asynchronous job dispatching and processing operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when handlerName is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no handler is found for the handler name.</exception>
    Task DispatchAsync(
        Guid jobId,
        string handlerName,
        ReadOnlyMemory<byte> jobPayload,
        CancellationToken cancellationToken = default);
}

