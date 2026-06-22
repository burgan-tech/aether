using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob;

/// <inheritdoc />
public class JobDispatcher(
    IServiceScopeFactory scopeFactory,
    BackgroundJobOptions options,
    IClock clock,
    IEventSerializer eventSerializer,
    ILogger<JobDispatcher> logger)
    : IJobDispatcher
{
    /// <inheritdoc/>
    public virtual async Task DispatchAsync(
        Guid jobId,
        string handlerName,
        ReadOnlyMemory<byte> jobPayload,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID cannot be empty.", nameof(jobId));

        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "BackgroundJob.Dispatch",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.id", jobId.ToString());
        activity?.SetTag("job.handler_name", handlerName);

        await using var scope = scopeFactory.CreateAsyncScope();
        
        var argsPayload = CloudEventEnvelopeHelper.ExtractDataPayload(eventSerializer, jobPayload, out var envelope);

        IDisposable? schemaScope = null;
        if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Schema))
        {
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            schemaScope = currentSchema.Change(envelope.Schema);
        }

        using (schemaScope)
        {
            await DispatchCoreAsync(scope, jobId, handlerName, argsPayload, activity, cancellationToken);
        }
    }

    private async Task DispatchCoreAsync(
        AsyncServiceScope scope,
        Guid jobId,
        string handlerName,
        ReadOnlyMemory<byte> argsPayload,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        string? jobName = null;
        JobKind kind = JobKind.OneShot;
        int retryCount = 0, maxRetry = 0;

        // --- Phase 1: load + atomic claim (one UoW) ---
        await using (var claimUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            var jobInfo = await jobStore.GetAsync(jobId, cancellationToken);
            if (jobInfo == null)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                await claimUow.CommitAsync(cancellationToken);
                return;
            }

            jobName = jobInfo.JobName;
            kind = jobInfo.Kind;
            retryCount = jobInfo.RetryCount;
            maxRetry = jobInfo.MaxRetryCount;
            activity?.SetTag("job.name", jobName);

            // Terminal already → nothing to do, ensure scheduler stops triggering, return.
            if (jobInfo.Status is BackgroundJobStatus.Completed or BackgroundJobStatus.Cancelled
                or BackgroundJobStatus.Failed)
            {
                activity?.SetTag("job.status", jobInfo.Status.ToString().ToLowerInvariant());
                activity?.SetStatus(ActivityStatusCode.Ok);
                await claimUow.CommitAsync(cancellationToken);
                await TryDeleteFromSchedulerAsync(jobScheduler, handlerName, jobName, cancellationToken);
                return;
            }

            if (!options.Invokers.ContainsKey(handlerName))
            {
                logger.LogWarning("No handler found for handler name '{HandlerName}' with job id '{JobId}'", handlerName,
                    jobId);
                await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Failed, clock.UtcNow,
                    "No handler found for handler type", cancellationToken);
                activity?.SetTag("job.status", "failed");
                activity?.SetStatus(ActivityStatusCode.Error, "No handler found for handler type");
                await claimUow.CommitAsync(cancellationToken);
                await TryDeleteFromSchedulerAsync(jobScheduler, handlerName, jobName, cancellationToken);
                return;
            }

            // Atomic claim: only one worker wins Scheduled→Running.
            var claimed = await jobStore.TryTransitionStatusAsync(jobId, BackgroundJobStatus.Scheduled,
                BackgroundJobStatus.Running, cancellationToken);
            await claimUow.CommitAsync(cancellationToken);
            if (!claimed)
            {
                logger.LogInformation(
                    "Job id '{JobId}' was not in Scheduled state (already claimed or late delivery); skipping", jobId);
                activity?.SetTag("job.status", "skipped");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }
        }

        // --- Phase 2: run handler (own UoW) + record outcome (separate UoW) ---
        try
        {
            await using (var runUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                await InvokeHandlerAsync(scope.ServiceProvider, handlerName, argsPayload, cancellationToken);
                await runUow.CommitAsync(cancellationToken);
            }

            await using (var doneUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                if (kind == JobKind.Recurring)
                    await jobStore.MarkRecurringRanAsync(jobId, clock.UtcNow, null, cancellationToken); // → Scheduled, LastRunAt
                else
                    await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Completed, clock.UtcNow,
                        cancellationToken: cancellationToken);
                await doneUow.CommitAsync(cancellationToken);
            }

            logger.LogInformation("Successfully completed handler '{HandlerName}' for job id '{JobId}'", handlerName,
                jobId);
            activity?.SetTag("job.status", kind == JobKind.Recurring ? "scheduled" : "completed");
            activity?.SetStatus(ActivityStatusCode.Ok);

            if (kind == JobKind.OneShot)
                await TryDeleteFromSchedulerAsync(jobScheduler, handlerName, jobName, cancellationToken); // recurring stays armed
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Handler '{HandlerName}' for job id '{JobId}' was cancelled", handlerName, jobId);
            activity?.SetTag("job.status", "cancelled");
            activity?.SetStatus(ActivityStatusCode.Ok);
            await MarkJobStatusAsync(uowManager, jobStore, jobId, BackgroundJobStatus.Cancelled,
                "Job was cancelled", cancellationToken);
            await TryDeleteFromSchedulerAsync(jobScheduler, handlerName, jobName, cancellationToken);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}".Truncate(4000);
            logger.LogError(ex, "Handler '{HandlerName}' for job id '{JobId}' failed", handlerName, jobId);
            activity?.SetTag("job.status", "failed");
            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
                    { "exception.message", ex.Message },
                }));
            }

            await using (var failUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                if (kind == JobKind.Recurring)
                {
                    // Recurring jobs rely on the next cron occurrence; record the error, return to Scheduled.
                    await jobStore.MarkRecurringRanAsync(jobId, clock.UtcNow, error, cancellationToken);
                }
                else if (retryCount + 1 <= maxRetry)
                {
                    var nextRetryAt = clock.UtcNow + ComputeBackoff(retryCount, options);
                    await jobStore.MarkRetryingAsync(jobId, nextRetryAt, error, cancellationToken); // → Retrying; poller re-arms
                }
                else
                {
                    await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Failed, clock.UtcNow, error,
                        cancellationToken);
                }

                await failUow.CommitAsync(cancellationToken);
            }

            // One-shot terminal (Failed) and recurring stay armed in Dapr; a Retrying one-shot is re-armed by the
            // poller, so only delete from the scheduler when the one-shot is terminally Failed.
            if (kind == JobKind.OneShot && retryCount + 1 > maxRetry)
                await TryDeleteFromSchedulerAsync(jobScheduler, handlerName, jobName, cancellationToken);
        }
    }

    /// <summary>
    /// Computes the exponential backoff delay before a one-shot job's next retry attempt.
    /// </summary>
    private static TimeSpan ComputeBackoff(int retryCount, BackgroundJobOptions options)
    {
        // Exponential: base * 2^retryCount, capped at 1 hour.
        var factor = Math.Pow(2, retryCount);
        var delayTicks = options.RetryBaseDelay.Ticks * factor;
        var capped = Math.Min(delayTicks, TimeSpan.FromHours(1).Ticks);
        return TimeSpan.FromTicks((long)capped);
    }

    /// <summary>
    /// Attempts to delete the job from the scheduler (e.g. Dapr) so it stops triggering.
    /// Logs a warning and does not throw if delete fails; DB state remains consistent.
    /// </summary>
    private async Task TryDeleteFromSchedulerAsync(
        IJobScheduler jobScheduler,
        string handlerName,
        string? jobName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            return;

        try
        {
            await jobScheduler.DeleteAsync(handlerName, jobName, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete job '{JobName}' from scheduler; job may continue to trigger until manually removed", jobName);
        }
    }

    /// <summary>
    /// Marks job status within a separate UoW to ensure status update is persisted
    /// even if the main transaction failed.
    /// </summary>
    private async Task MarkJobStatusAsync(
        IUnitOfWorkManager uowManager,
        IJobStore jobStore,
        Guid jobId,
        BackgroundJobStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var uow = uowManager.BeginRequiresNew();
            await jobStore.UpdateStatusAsync(jobId, status, clock.UtcNow, errorMessage, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark job {JobId} as {Status}", jobId, status);
        }
    }

    /// <summary>
    /// Invokes the handler using pre-created invoker (no runtime reflection).
    /// Generic type TArgs was closed at registration time (startup), not at runtime.
    /// This method is completely type-safe and fast.
    /// </summary>
    private async Task InvokeHandlerAsync(IServiceProvider scopedProvider, string handlerName,
        ReadOnlyMemory<byte> jobPayload,
        CancellationToken cancellationToken)
    {
        // Get pre-created invoker from options (generic already closed at startup)
        if (!options.Invokers.TryGetValue(handlerName, out var invoker))
        {
            throw new InvalidOperationException($"No invoker registered for handler '{handlerName}'");
        }

        // Invoke handler - completely type-safe, no reflection at runtime
        await invoker.InvokeAsync(scopedProvider, eventSerializer, jobPayload, cancellationToken);
    }
}
