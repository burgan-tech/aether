using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Defines the contract for scheduling and managing background jobs with an underlying scheduler.
/// This abstraction allows for pluggable scheduler backends (e.g., Dapr, Quartz, Hangfire).
/// </summary>
public interface IJobScheduler
{
    /// <summary>
    /// Schedules a new background job with the specified parameters.
    /// </summary>
    /// <param name="handlerName">The name of the handler type (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="jobName">The unique job name for the external scheduler (e.g., "send-email-order-123").</param>
    /// <param name="schedule">The schedule expression (e.g., cron expression or delay).</param>
    /// <param name="payload">The serialized job payload data.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous scheduling operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the scheduler is unavailable or scheduling fails.</exception>
    Task ScheduleAsync(
        string handlerName,
        string jobName,
        string schedule,
        ReadOnlyMemory<byte> payload,
        JobScheduleFailurePolicy? failurePolicyOptions = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Arms a one-time trigger that fires once at <paramref name="dueAtUtc"/>. Used for retry re-arming
    /// (exponential backoff) and one-shot delayed jobs. Idempotent (overwrites any existing job with the
    /// same name).
    /// </summary>
    /// <param name="handlerName">The name of the handler type (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="jobName">The unique job name for the external scheduler (e.g., "send-email-order-123").</param>
    /// <param name="dueAtUtc">The UTC instant at which the job should fire exactly once.</param>
    /// <param name="payload">The serialized job payload data.</param>
    /// <param name="failurePolicy">Optional failure policy applied to the armed trigger.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous scheduling operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the scheduler is unavailable or scheduling fails.</exception>
    Task ScheduleOneShotAsync(
        string handlerName,
        string jobName,
        DateTime dueAtUtc,
        ReadOnlyMemory<byte> payload,
        JobScheduleFailurePolicy? failurePolicy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a scheduled background job from the scheduler.
    /// </summary>
    /// <param name="handlerName">The name of the handler type.</param>
    /// <param name="jobName">The unique job name for the external scheduler.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous delete operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null or empty.</exception>
    Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default);
}

