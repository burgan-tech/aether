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
    /// Dispatches a background job to the appropriate handler. The dispatcher resolves the job by its
    /// name (single read), atomically claims it (Scheduled→Running), runs the handler with no
    /// dispatcher-owned transaction, and records the outcome in a short unit of work.
    /// </summary>
    /// <param name="jobName">The unique job name (the scheduler's job identifier) used to resolve the job entity.</param>
    /// <param name="jobPayload">The serialized job payload data to be processed by the handler.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests during job processing.</param>
    /// <returns>A task representing the asynchronous job dispatching and processing operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when jobName is null or empty.</exception>
    Task DispatchAsync(
        string jobName,
        ReadOnlyMemory<byte> jobPayload,
        CancellationToken cancellationToken = default);
}

