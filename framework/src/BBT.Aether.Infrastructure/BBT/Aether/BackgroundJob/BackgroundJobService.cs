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
    ILogger<BackgroundJobService> logger)
    : IBackgroundJobService
{
    private const string Source = "urn:background-job";

    /// <inheritdoc/>
    public async Task<Guid> EnqueueAsync<TPayload>(
        string handlerName,
        string jobName,
        TPayload payload,
        string schedule,
        Dictionary<string, object>? metadata = null,
        JobScheduleFailurePolicy? failurePolicyOptions = null,
        bool useAmbientUnitOfWork = false,
        Guid? jobId = null,
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

        var jobInfo = new BackgroundJobInfo(effectiveJobId, handlerName, jobName)
        {
            ExpressionValue = schedule,
            Payload = eventSerializer.SerializeToElement(envelope),
            Status = BackgroundJobStatus.Scheduled,
            ExtraProperties = extraProperties
        };

        // Serialize envelope for scheduler
        var payloadBytes = eventSerializer.Serialize(envelope);

        // Transactional enqueue: when the caller has an ambient unit of work and opts in, persist the
        // job record INTO that ambient UoW (no own RequiresNew transaction, no self-commit) and defer
        // the scheduler call to its OnCompleted. The job row then commits atomically with the caller's
        // business transaction and the scheduler only fires after a successful commit — avoiding
        // nested-UoW / shared-DbContext collisions ("A second operation was started on this context
        // instance") and orphaned scheduled jobs on rollback.
        if (useAmbientUnitOfWork && uowManager.Current is { } ambientUow)
        {
            await jobStore.SaveAsync(jobInfo, cancellationToken);

            ambientUow.OnCompleted(async _ =>
            {
                try
                {
                    await jobScheduler.ScheduleAsync(
                        handlerName, jobName, schedule, payloadBytes, failurePolicyOptions, cancellationToken);

                    logger.LogInformation(
                        "Successfully scheduled job handler '{HandlerName}' with job name '{JobName}'. Entity ID: {EntityId}",
                        handlerName, jobName, effectiveJobId);
                }
                catch (Exception ex)
                {
                    // The business transaction is already committed but the scheduler call failed.
                    // OnCompleted handler exceptions are swallowed by the UoW, so log loudly here —
                    // recovery (re-enqueue of the pending job intent) is the caller's responsibility.
                    logger.LogError(ex,
                        "Failed to schedule job handler '{HandlerName}' with job name '{JobName}' after commit. Entity ID: {EntityId}",
                        handlerName, jobName, effectiveJobId);
                }
            });

            activity?.SetStatus(ActivityStatusCode.Ok);
            return effectiveJobId;
        }

        await using var uow = uowManager.Begin(
            options: new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
        try
        {
            // Save to job store
            await jobStore.SaveAsync(jobInfo, cancellationToken);
            await uow.SaveChangesAsync(cancellationToken);
            // Register scheduler to run AFTER commit is fully persisted to DB
            // This prevents race condition where scheduler triggers before DB write completes
            uow.OnCompleted(async _ =>
            {
                await jobScheduler.ScheduleAsync(handlerName, jobName, schedule, payloadBytes, failurePolicyOptions, cancellationToken);
                
                logger.LogInformation(
                    "Successfully scheduled job handler '{HandlerName}' with job name '{JobName}'. Entity ID: {EntityId}",
                    handlerName, jobName, effectiveJobId);
            });
        
            // Commit transaction - OnCompleted handlers run after this completes
            await uow.CommitAsync(cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return effectiveJobId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue job handler '{HandlerName}' with job name '{JobName}'", handlerName,
                jobName);
            RecordException(activity, ex);
            await uow.RollbackAsync(cancellationToken);
            throw;
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

        await using var uow = uowManager.Begin();
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

            // Update schedule in entity
            jobInfo.ExpressionValue = newSchedule;

            // Delete from scheduler and reschedule with new schedule
            await jobScheduler.DeleteAsync(jobInfo.HandlerName, jobInfo.JobName, cancellationToken);

            // Wrap payload in CloudEventEnvelope (maintain schema context from original job)
            var originalPayload = jobInfo.Payload;
            var envelope = new CloudEventEnvelope
            {
                Type = jobInfo.HandlerName,
                Source = "background-job",
                Data = originalPayload,
                Schema = currentSchema.Name, // Use current schema context
                DataContentType = "application/json"
            };

            // Reschedule with new schedule
            var payloadBytes = eventSerializer.Serialize(envelope);
            await jobScheduler.ScheduleAsync(jobInfo.HandlerName, jobInfo.JobName, newSchedule, payloadBytes,
                cancellationToken: cancellationToken);

            // Save updated entity
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
