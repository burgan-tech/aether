using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Generic implementation of the background job service.
/// Scheduler-agnostic: Works with any IJobScheduler implementation (Dapr, Quartz, Hangfire, etc.).
/// Integrates job persistence via IJobStore with scheduling via IJobScheduler.
/// Uses UoW pattern for transactional consistency.
/// Wraps job payloads in CloudEventEnvelope to carry schema context and metadata.
/// </summary>
public sealed class BackgroundJobService(
    IJobStore jobStore,
    IJobScheduler jobScheduler,
    IUnitOfWorkManager uowManager,
    IGuidGenerator guidGenerator,
    IClock clock,
    ICurrentSchema currentSchema,
    IEventSerializer eventSerializer,
    BackgroundJobOptions options,
    ILogger<BackgroundJobService> logger)
    : IBackgroundJobService
{
    private const string Source = "urn:background-job";

    /// <summary>
    /// Infers the <see cref="JobKind"/> from a raw schedule string when the caller does not specify one.
    /// Heuristic: a trimmed value starting with <c>@</c> (e.g. <c>@every 5s</c>, <c>@daily</c>) or one
    /// composed of 5–6 whitespace-separated fields (a standard cron expression) is treated as
    /// <see cref="JobKind.Recurring"/>; everything else (e.g. an ISO-8601 instant, a duration) is
    /// <see cref="JobKind.OneShot"/>.
    /// </summary>
    /// <param name="schedule">The raw schedule expression.</param>
    /// <returns>The inferred job kind.</returns>
    private static JobKind InferKind(string schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return JobKind.OneShot;

        var trimmed = schedule.Trim();

        // "@every 1h", "@daily", "@hourly", ... are recurring period expressions.
        if (trimmed.StartsWith('@'))
            return JobKind.Recurring;

        // Standard cron expressions have 5 (minute..weekday) or 6 (with seconds) whitespace-separated fields.
        var fieldCount = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (fieldCount is 5 or 6)
            return JobKind.Recurring;

        // Single token / ISO-8601 instant / duration → fires once.
        return JobKind.OneShot;
    }

    /// <inheritdoc/>
    public async Task<Guid> EnqueueAsync<TPayload>(
        string handlerName,
        string jobName,
        TPayload payload,
        string schedule,
        Dictionary<string, object>? metadata = null,
        JobScheduleFailurePolicy? failurePolicyOptions = null,
        bool directly = false,
        Guid? jobId = null,
        JobKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        if (payload == null)
            throw new ArgumentNullException(nameof(payload));

        if (string.IsNullOrWhiteSpace(schedule))
            throw new ArgumentNullException(nameof(schedule));

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "BackgroundJob.Enqueue",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.handler_name", handlerName);
        activity?.SetTag("job.name", jobName);
        activity?.SetTag("job.schedule", schedule);

        logger.LogInformation(
            "Enqueueing job handler '{HandlerName}' with job name '{JobName}' and schedule '{Schedule}'",
            handlerName, jobName, schedule);

        // Create job entity. A caller-supplied id (when present) lets the caller reuse a single
        // correlation id for its own tracking row; otherwise generate one.
        var effectiveJobId = jobId ?? guidGenerator.Create();
        activity?.SetTag("job.id", effectiveJobId.ToString());

        // Convert metadata to nullable dictionary for ExtraPropertyDictionary
        var extraProperties = new ExtraPropertyDictionary();
        if (metadata != null)
        {
            foreach (var kvp in metadata)
            {
                extraProperties[kvp.Key] = kvp.Value;
            }
        }

        var envelope = new CloudEventEnvelope<TPayload>
        {
            Type = handlerName,
            Source = Source,
            Data = payload,
            Schema = currentSchema.Name,
            DataContentType = "application/json"
        };

        // Determine the job kind: explicit caller value wins, otherwise infer from the schedule.
        var effectiveKind = kind ?? InferKind(schedule);
        activity?.SetTag("job.kind", effectiveKind.ToString());

        // Build the row. When directly:true, save as Scheduled so the arming poller cannot race and
        // double-schedule. ArmNowAsync calls the scheduler; on failure it rolls Scheduled→Pending for
        // the poller to retry. When directly:false (default), save as Pending and let the arming poller
        // arm it after the caller's transaction commits — no orphaned scheduled job on rollback.
        var jobInfo = new BackgroundJobInfo(effectiveJobId, handlerName, jobName)
        {
            ExpressionValue = schedule,
            Payload = eventSerializer.SerializeToElement(envelope),
            Status = directly ? BackgroundJobStatus.Scheduled : BackgroundJobStatus.Pending,
            Kind = effectiveKind,
            MaxRetryCount = options.MaxRetryCount,
            NextRetryAt = clock.UtcNow,
            ExtraProperties = extraProperties
        };

        // TODO(jobs): thread failurePolicyOptions through the arming poller. The poller currently arms
        // with a default failure policy; the caller-supplied policy is not yet persisted/honored. Kept in
        // the signature so callers don't break and so a later task can wire it through ExtraProperties.
        // The `directly` path below DOES honor failurePolicyOptions, via ScheduleAsync.

        // Bytes for the scheduler (the `directly` arm path). Equivalent to the JSON the poller arms with.
        var payloadBytes = eventSerializer.Serialize(envelope);

        // Atomic-ambient: when the caller has an ambient UoW, persist into it (commits with their business
        // transaction — a rollback discards the row). Otherwise open a short own transaction.
        if (uowManager.Current is { } ambient)
        {
            await jobStore.SaveAsync(jobInfo, cancellationToken);
            if (directly)
                ambient.OnCompleted(_ => ArmNowAsync(handlerName, jobName, schedule, payloadBytes,
                    failurePolicyOptions, effectiveJobId, CancellationToken.None));
            logger.LogInformation(
                "Enqueued Pending job '{HandlerName}'/'{JobName}' into ambient UoW. Id: {Id}",
                handlerName, jobName, effectiveJobId);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return effectiveJobId;
        }

        await using (var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            try
            {
                await jobStore.SaveAsync(jobInfo, cancellationToken);
                await uow.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to enqueue job '{HandlerName}'/'{JobName}'", handlerName, jobName);
                RecordException(activity, ex);
                await uow.RollbackAsync(cancellationToken);
                throw;
            }
        }
        if (directly)
            await ArmNowAsync(handlerName, jobName, schedule, payloadBytes, failurePolicyOptions,
                effectiveJobId, cancellationToken);
        logger.LogInformation(
            "Enqueued {Status} job '{HandlerName}'/'{JobName}'. Id: {Id}",
            jobInfo.Status, handlerName, jobName, effectiveJobId);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return effectiveJobId;
    }

    /// <summary>
    /// Calls the scheduler inline and, on failure, rolls the row back from Scheduled to Pending so the
    /// arming poller acts as the backstop. Never throws — failure is logged as a warning.
    /// </summary>
    private async Task ArmNowAsync(string handlerName, string jobName, string schedule,
        ReadOnlyMemory<byte> payloadBytes, JobScheduleFailurePolicy? failurePolicy, Guid jobId, CancellationToken ct)
    {
        try
        {
            await jobScheduler.ScheduleAsync(handlerName, jobName, schedule, payloadBytes, failurePolicy, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Immediate (directly) arm failed for job '{JobName}'; rolling back to Pending for arming poller",
                jobName);
            IUnitOfWork? rollbackUow = null;
            try
            {
                rollbackUow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                await jobStore.TryTransitionStatusAsync(jobId, BackgroundJobStatus.Scheduled, BackgroundJobStatus.Pending, ct);
                await rollbackUow.CommitAsync(ct);
            }
            catch (Exception rollbackEx)
            {
                logger.LogError(rollbackEx,
                    "Failed to roll back job '{JobName}' to Pending; arming poller will arm it on next visibility-timeout pass",
                    jobName);
                if (rollbackUow != null)
                    await rollbackUow.RollbackAsync(ct);
            }
            finally
            {
                if (rollbackUow is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
            }
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(Guid id, string newSchedule, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        if (string.IsNullOrWhiteSpace(newSchedule))
            throw new ArgumentNullException(nameof(newSchedule));

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "BackgroundJob.Update",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.id", id.ToString());
        activity?.SetTag("job.schedule", newSchedule);

        logger.LogInformation("Updating job with entity id '{Id}' to new schedule '{NewSchedule}'", id, newSchedule);

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
        try
        {
            // Load job from store
            var jobInfo = await jobStore.GetAsync(id, cancellationToken);
            if (jobInfo == null)
            {
                throw new InvalidOperationException($"Job with id '{id}' not found.");
            }

            activity?.SetTag("job.handler_name", jobInfo.HandlerName);
            activity?.SetTag("job.name", jobInfo.JobName);

            // Reschedule by handing the row back to the arming poller: set the new schedule, recompute the
            // kind, mark it Pending and due now. The poller re-arms it in the scheduler (overwrite: true,
            // so the new schedule replaces the old). We do NOT call the scheduler here and we do NOT touch
            // Payload — it already holds the original envelope with the original schema context, which the
            // poller reuses. This avoids the previous bugs: wrong-schema re-wrap, double-wrapping the
            // payload, losing the failure policy, and the delete/reschedule race.
            jobInfo.ExpressionValue = newSchedule;
            jobInfo.Kind = InferKind(newSchedule);
            jobInfo.Status = BackgroundJobStatus.Pending;
            jobInfo.NextRetryAt = clock.UtcNow;

            await jobStore.SaveAsync(jobInfo, cancellationToken);

            // Commit transaction
            await uow.CommitAsync(cancellationToken);

            logger.LogInformation("Successfully updated job with entity id '{Id}'", id);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update job with entity id '{Id}'", id);
            RecordException(activity, ex);
            await uow.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "BackgroundJob.Delete",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.id", id.ToString());

        logger.LogInformation("Deleting job with entity id '{Id}'", id);

        await using var uow = uowManager.Begin();
        try
        {
            // Load job from store
            var jobInfo = await jobStore.GetAsync(id, cancellationToken);
            if (jobInfo == null)
            {
                logger.LogWarning("Job with entity id '{Id}' not found", id);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return false;
            }

            activity?.SetTag("job.handler_name", jobInfo.HandlerName);
            activity?.SetTag("job.name", jobInfo.JobName);

            // Delete from scheduler
            await jobScheduler.DeleteAsync(jobInfo.HandlerName, jobInfo.JobName, cancellationToken);

            // Update status to Cancelled
            await jobStore.UpdateStatusAsync(id, BackgroundJobStatus.Cancelled, clock.UtcNow,
                cancellationToken: cancellationToken);

            // Commit transaction
            await uow.CommitAsync(cancellationToken);

            logger.LogInformation("Successfully deleted job with entity id '{Id}'", id);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete job with entity id '{Id}'", id);
            RecordException(activity, ex);
            await uow.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }
}
