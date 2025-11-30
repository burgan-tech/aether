using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Provides functionality for scheduling, updating, and managing background jobs.
/// This service integrates with the job scheduler infrastructure to manage workflow-related background tasks.
/// </summary>
public interface IBackgroundJobService
{
    /// <summary>
    /// Enqueues a background job with the specified parameters and schedule.
    /// Creates a new job entity, persists it, and schedules it with the underlying scheduler.
    /// </summary>
    /// <typeparam name="TPayload">The type of the job payload.</typeparam>
    /// <param name="handlerName">The name of the handler type to execute (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="jobName">A unique job name for the external scheduler (e.g., "send-email-order-123").</param>
    /// <param name="payload">The data payload to be passed to the job handler when executed.</param>
    /// <param name="schedule">The schedule expression defining when the job should be executed (e.g., cron expression).</param>
    /// <param name="metadata">Additional metadata associated with the job (optional).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous enqueue operation.
    /// The result contains the entity ID (Guid) of the created job.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job scheduling service is unavailable.</exception>
    Task<Guid> EnqueueAsync<TPayload>(
        string handlerName,
        string jobName,
        TPayload payload,
        string schedule,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the schedule of an existing background job.
    /// </summary>
    /// <param name="id">The entity ID of the job to update.</param>
    /// <param name="newSchedule">The new schedule expression for the job.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous update operation.</returns>
    /// <exception cref="ArgumentException">Thrown when id is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job is not found or cannot be updated.</exception>
    Task UpdateAsync(Guid id, string newSchedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a scheduled background job.
    /// Removes the job from the scheduler and updates the job status to Cancelled.
    /// </summary>
    /// <param name="id">The entity ID of the job to delete.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous delete operation.
    /// The result is true if the job was successfully deleted; otherwise, false.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when id is empty.</exception>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
