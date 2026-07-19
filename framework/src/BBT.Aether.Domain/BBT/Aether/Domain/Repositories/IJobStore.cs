using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Defines the contract for persisting and retrieving background job information.
/// This interface provides data access operations for managing job state,
/// scheduling information, and execution tracking.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// Saves or updates background job information in the persistent store.
    /// If a job with the same ID already exists, it will be updated; otherwise, a new record is created.
    /// </summary>
    /// <param name="jobInfo">The complete job information including payload, metadata, and scheduling details.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when jobInfo is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job cannot be saved due to storage issues.</exception>
    Task SaveAsync(BackgroundJobInfo jobInfo, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves background job information by the entity identifier.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job to retrieve.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains the job information if found; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when id is empty.</exception>
    Task<BackgroundJobInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current persisted cancellation fields without consulting a tracked entity instance.
    /// Use this after a failed set-based cancellation update so concurrent Running or terminal transitions
    /// are classified from fresh database state.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The current cancellation snapshot, or null when the job does not exist.</returns>
    Task<BackgroundJobCancellationSnapshot?> GetCancellationSnapshotAsync(
        Guid id,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves active background job information by the job name (external scheduler identifier).
    /// </summary>
    /// <param name="jobName">The unique job name used by the external scheduler (e.g., "send-email-order-123").</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains the job information if found; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when jobName is null or empty.</exception>
    Task<BackgroundJobInfo?> GetByJobNameAsync(string jobName, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves a collection of background jobs by handler name.
    /// </summary>
    /// <param name="handlerName">The name of the handler type to retrieve (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains a collection of job information matching the handler name.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when handlerName is null or empty.</exception>
    Task<IEnumerable<BackgroundJobInfo>> GetByHandlerNameAsync(string handlerName, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves a collection of all active background jobs (Scheduled or Running status).
    /// This method is typically used for job recovery scenarios or monitoring purposes.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains a collection of active job information.
    /// </returns>
    /// <remarks>
    /// Active jobs are those in Scheduled or Running status.
    /// This method excludes jobs that are Completed, Failed, or Cancelled.
    /// </remarks>
    Task<IEnumerable<BackgroundJobInfo>> GetActiveAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates the status of a background job along with related metadata.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job to update.</param>
    /// <param name="status">The new status to set.</param>
    /// <param name="handledTime">The time when the job was handled (for Completed/Failed status).</param>
    /// <param name="error">The error message (for Failed status).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous update operation.</returns>
    /// <exception cref="ArgumentException">Thrown when id is empty.</exception>
    Task UpdateStatusAsync(
        Guid id,
        BackgroundJobStatus status,
        DateTime? handledTime = null,
        string? error = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically transitions a job from one status to another. Returns true iff a row was
    /// updated (false ⇒ the job was not in <paramref name="from"/> — another worker moved it first,
    /// the concurrency guard).
    /// </summary>
    /// <param name="id">The unique entity identifier of the job to transition.</param>
    /// <param name="from">The status the job must currently be in for the transition to apply.</param>
    /// <param name="to">The status to set the job to.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>True if a row was updated; otherwise false.</returns>
    Task<bool> TryTransitionStatusAsync(Guid id, BackgroundJobStatus from, BackgroundJobStatus to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Jobs due for arming: status Pending, or Retrying with NextRetryAt &lt;= nowUtc.
    /// Ordered by NextRetryAt ascending, limited to batchSize.
    /// </summary>
    /// <param name="nowUtc">The current UTC time used to evaluate retry due-ness.</param>
    /// <param name="batchSize">The maximum number of jobs to return.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The due jobs, ordered by NextRetryAt ascending.</returns>
    Task<IReadOnlyList<BackgroundJobInfo>> GetDueForArmingAsync(DateTime nowUtc, int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed one-shot attempt: increments RetryCount, sets NextRetryAt + LastError,
    /// HandledTime, and status = Retrying.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job.</param>
    /// <param name="nextRetryAtUtc">The UTC time at which the job should next be armed.</param>
    /// <param name="error">The error message from the failed attempt, if any.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MarkRetryingAsync(Guid id, DateTime nextRetryAtUtc, string? error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a recurring job's run: status back to Scheduled, sets LastRunAt, increments
    /// RetryCount and sets LastError only when <paramref name="error"/> is non-null.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job.</param>
    /// <param name="ranAtUtc">The UTC time at which the job ran.</param>
    /// <param name="error">The error message from the run, if any.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task MarkRecurringRanAsync(Guid id, DateTime ranAtUtc, string? error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a Scheduled job for execution: sets Status=Running and stamps
    /// RunningSince=nowUtc in a single conditional UPDATE. Returns true iff this caller won the claim
    /// (false ⇒ the job was not Scheduled — already claimed or a late delivery).
    /// </summary>
    /// <param name="id">The unique entity identifier of the job to claim.</param>
    /// <param name="nowUtc">The current UTC time stamped into RunningSince when the claim succeeds.</param>
    /// <param name="runningToken">A fresh per-claim token stamped into RunningToken; later identifies this lease.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>True if this caller won the claim; otherwise false.</returns>
    Task<bool> TryClaimAsync(Guid id, DateTime nowUtc, Guid runningToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically cancels a Pending, Scheduled, or Retrying job.
    /// Running and terminal jobs are not changed.
    /// </summary>
    Task<bool> TryCancelWaitingAsync(
        Guid id,
        DateTime handledTimeUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records a terminal outcome (Completed/Failed/Cancelled) for a Running job, guarded on the
    /// claim token: updates only when <c>Status==Running &amp;&amp; RunningToken==runningToken</c>. Clears
    /// RunningSince/RunningToken. Returns true iff a row was updated (false ⇒ the claim was lost — the job is
    /// no longer Running under this token, e.g. the reaper already reset it).
    /// </summary>
    /// <param name="id">The unique entity identifier of the job.</param>
    /// <param name="runningToken">The claim token observed when the job was claimed.</param>
    /// <param name="terminalStatus">The terminal status to set (Completed, Failed, or Cancelled).</param>
    /// <param name="handledTimeUtc">The UTC time the job was handled.</param>
    /// <param name="error">The error message, if any (preserves the existing value when null).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>True if a row was updated; otherwise false.</returns>
    Task<bool> TryRecordTerminalAsync(Guid id, Guid runningToken, BackgroundJobStatus terminalStatus,
        DateTime handledTimeUtc, string? error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically returns a Running job to Scheduled (for recurring jobs), guarded on the claim token:
    /// updates only when <c>Status==Running &amp;&amp; RunningToken==runningToken</c>. Sets LastRunAt, clears
    /// RunningSince/RunningToken, and does NOT increment RetryCount. Returns true iff a row was updated
    /// (false ⇒ the claim was lost).
    /// </summary>
    /// <param name="id">The unique entity identifier of the job.</param>
    /// <param name="runningToken">The claim token observed when the job was claimed.</param>
    /// <param name="ranAtUtc">The UTC time the job ran.</param>
    /// <param name="error">The error message, if any (preserves the existing value when null).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>True if a row was updated; otherwise false.</returns>
    Task<bool> TryReturnToScheduledAsync(Guid id, Guid runningToken, DateTime ranAtUtc, string? error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically transitions a Running job to Retrying (for one-shot jobs), guarded on the claim token:
    /// updates only when <c>Status==Running &amp;&amp; RunningToken==runningToken</c>. Increments RetryCount
    /// exactly once, sets NextRetryAt + LastError, and clears RunningSince/RunningToken. Returns true iff a
    /// row was updated (false ⇒ the claim was lost).
    /// </summary>
    /// <param name="id">The unique entity identifier of the job.</param>
    /// <param name="runningToken">The claim token observed when the job was claimed.</param>
    /// <param name="nextRetryAtUtc">The UTC time at which the job should next be armed.</param>
    /// <param name="error">The error message from the failed attempt. Unlike the terminal/return-to-scheduled
    /// methods this OVERWRITES LastError (a retry's error is always the current attempt's failure).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>True if a row was updated; otherwise false.</returns>
    Task<bool> TryMarkRetryingAsync(Guid id, Guid runningToken, DateTime nextRetryAtUtc, string? error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Jobs stuck in Running since before cutoffUtc (crashed/timed-out executions), oldest first,
    /// limited to batchSize. Used by the visibility-timeout reaper.
    /// </summary>
    /// <param name="cutoffUtc">The UTC cutoff; jobs with RunningSince before this are considered stale.</param>
    /// <param name="batchSize">The maximum number of jobs to return.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The stale running jobs, ordered by RunningSince ascending.</returns>
    Task<IReadOnlyList<BackgroundJobInfo>> GetStaleRunningAsync(DateTime cutoffUtc, int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy arming transition contract. Existing store implementers continue to implement this
    /// token-guarded overload; new framework code uses the status-guarded overload below.
    /// </summary>
    Task<bool> TryTransitionFromArmingAsync(
        Guid id,
        Guid armingToken,
        BackgroundJobStatus to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears <see cref="BackgroundJobInfo.ArmingToken"/>/<see cref="BackgroundJobInfo.ArmingUntil"/>
    /// and transitions the job to <paramref name="to"/>, guarded on both the arming token and
    /// <paramref name="expectedOriginalStatus"/>. Returns false if either no longer matches
    /// (another pod or cancellation already acted on this row, or the lease expired).
    /// </summary>
    Task<bool> TryTransitionFromArmingAsync(
        Guid id,
        Guid armingToken,
        BackgroundJobStatus expectedOriginalStatus,
        BackgroundJobStatus to,
        CancellationToken cancellationToken) =>
        TryTransitionFromArmingAsync(id, armingToken, to, cancellationToken);

    /// <summary>
    /// Atomically leases terminal scheduler cleanup by stamping <paramref name="compensationToken"/>.
    /// The claim succeeds only for a terminal row whose arming token is absent, belongs to the lost
    /// claim, or has expired. The default safely declines cleanup for legacy custom stores.
    /// </summary>
    Task<bool> TryAcquireTerminalArmingCompensationAsync(
        Guid id,
        Guid lostArmingToken,
        Guid compensationToken,
        DateTime now,
        DateTime compensationUntil,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Extends a terminal-cleanup lease while external deletion is in flight. The exact token guard
    /// prevents an old owner from renewing after another claimant has taken over.
    /// The default safely reports lease loss for legacy custom stores.
    /// </summary>
    Task<bool> TryRenewArmingCompensationAsync(
        Guid id,
        Guid compensationToken,
        DateTime compensationUntil,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Releases a terminal-cleanup lease using the exact compensation token. Status is intentionally
    /// not part of the guard: a concurrent update may have moved the row back to Pending while retaining
    /// the token, and must be allowed to arm after cleanup completes.
    /// </summary>
    Task<bool> TryReleaseArmingCompensationAsync(
        Guid id,
        Guid compensationToken,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Clears the arming claim fields (<see cref="BackgroundJobInfo.ArmingToken"/> and
    /// <see cref="BackgroundJobInfo.ArmingUntil"/>) for rows whose arming claim has expired
    /// (<see cref="BackgroundJobInfo.ArmingUntil"/> &lt; <paramref name="now"/>), restoring the
    /// row to its pre-claim state. <see cref="BackgroundJobInfo.Status"/> is intentionally left
    /// unchanged — the claim never modified it. Called by the arming-claim reaper.
    /// </summary>
    /// <returns>Number of rows reset.</returns>
    Task<int> ResetExpiredArmingClaimsAsync(
        DateTime now,
        int batchSize,
        CancellationToken cancellationToken = default);
}
