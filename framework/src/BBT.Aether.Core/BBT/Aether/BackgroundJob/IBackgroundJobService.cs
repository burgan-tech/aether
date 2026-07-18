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
    /// Creates and persists a new job entity. The poller normally schedules it; direct arming is optional.
    /// The job row is persisted atomically with the caller's ambient unit of work when one is active
    /// (a rollback discards it); otherwise a short transaction is opened and committed. The default path is
    /// armed later by the poller; when <paramref name="directly"/> is true, scheduler arming is attempted
    /// only after persistence commits.
    /// </summary>
    /// <typeparam name="TPayload">The type of the job payload.</typeparam>
    /// <param name="handlerName">The name of the handler type to execute (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="jobName">A unique job name for the external scheduler (e.g., "send-email-order-123").</param>
    /// <param name="payload">The data payload to be passed to the job handler when executed.</param>
    /// <param name="schedule">The schedule expression defining when the job should be executed (e.g., cron expression).</param>
    /// <param name="metadata">Additional metadata associated with the job (optional).</param>
    /// <param name="failurePolicyOptions">Retry/failure policy for the scheduled job (optional).</param>
    /// <param name="directly">
    /// When <c>true</c>, the row is initially persisted as Scheduled and scheduler arming is attempted after
    /// persistence commits, instead of waiting for the arming poller. In the ambient case the attempt runs
    /// from the UoW's <c>OnCompleted</c> callback; otherwise it runs after enqueue's own UoW commits. If the
    /// scheduler call fails, recovery from Scheduled to Pending is best-effort. A process crash, cancellation,
    /// or recovery failure can leave the row Scheduled without a scheduler entry. When <c>false</c> (default),
    /// the row is persisted as Pending and durable arming is left to the poller.
    /// </param>
    /// <param name="jobId">
    /// Optional caller-supplied entity id for the created job. When provided, <c>BackgroundJobInfo.Id</c>
    /// is set to this value (and it is the returned id), so the caller can generate one correlation id
    /// up front and reuse it for its own tracking row — avoiding a placeholder/mismatch and keeping
    /// cancellation-by-id reliable. When <c>null</c> (default) an id is generated internally. Ignored
    /// when a job with the same name already exists (the existing id is kept).
    /// </param>
    /// <param name="kind">
    /// Optional job kind (<see cref="BBT.Aether.Domain.Entities.JobKind.OneShot"/> vs
    /// <see cref="BBT.Aether.Domain.Entities.JobKind.Recurring"/>). When <c>null</c> (default) the kind is
    /// inferred from <paramref name="schedule"/>: a cron expression or an <c>@</c>-prefixed period
    /// (<c>@every</c>, <c>@daily</c>, ...) ⇒ Recurring; anything else (e.g. an ISO-8601 instant) ⇒ OneShot.
    /// </param>
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
        JobScheduleFailurePolicy? failurePolicyOptions = null,
        bool directly = false,
        Guid? jobId = null,
        BBT.Aether.Domain.Entities.JobKind? kind = null,
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
