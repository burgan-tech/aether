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
        string jobName,
        ReadOnlyMemory<byte> jobPayload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "BackgroundJob.Dispatch",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.name", jobName);

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
            await DispatchCoreAsync(scope, jobName, argsPayload, activity, cancellationToken);
        }
    }

    private async Task DispatchCoreAsync(
        AsyncServiceScope scope,
        string jobName,
        ReadOnlyMemory<byte> argsPayload,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        Guid jobId;
        string handlerName;
        JobKind kind;
        int retryCount, maxRetry;

        // --- Phase 1: single load + atomic claim (one UoW) ---
        await using (var claimUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            var jobInfo = await jobStore.GetByJobNameAsync(jobName, cancellationToken);
            if (jobInfo is null)
            {
                logger.LogWarning("Job '{JobName}' not found in an active state; skipping", jobName);
                activity?.SetStatus(ActivityStatusCode.Ok);
                await claimUow.CommitAsync(cancellationToken);
                return;
            }

            jobId = jobInfo.Id;
            handlerName = jobInfo.HandlerName;
            kind = jobInfo.Kind;
            retryCount = jobInfo.RetryCount;
            maxRetry = jobInfo.MaxRetryCount;
            activity?.SetTag("job.id", jobId.ToString());
            activity?.SetTag("job.handler_name", handlerName);

            if (!options.Invokers.ContainsKey(handlerName))
            {
                logger.LogWarning("No handler found for handler name '{HandlerName}' (job id '{JobId}')", handlerName,
                    jobId);
                await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Failed, clock.UtcNow,
                    "No handler found for handler type", cancellationToken);
                await claimUow.CommitAsync(cancellationToken);
                await TryDeleteFromSchedulerAsync(jobScheduler, handlerName, jobName, cancellationToken);
                activity?.SetTag("job.status", "failed");
                activity?.SetStatus(ActivityStatusCode.Error, "No handler");
                return;
            }

            // Atomic claim: only one worker wins Scheduled→Running (and stamps RunningSince).
            var claimed = await jobStore.TryClaimAsync(jobId, clock.UtcNow, cancellationToken);
            await claimUow.CommitAsync(cancellationToken);
            if (!claimed)
            {
                logger.LogInformation(
                    "Job id '{JobId}' was not Scheduled (already claimed or late delivery); skipping", jobId);
                activity?.SetTag("job.status", "skipped");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }
        }

        // --- Phase 2: run the handler with NO dispatcher transaction ---
        // The dispatcher holds no open UoW/connection across the handler. The handler owns its own UoW
        // boundaries (it can open IUnitOfWorkManager.Begin around its DB work). The schema scope is active.
        try
        {
            await InvokeHandlerAsync(scope.ServiceProvider, handlerName, argsPayload, cancellationToken);

            // --- Phase 3: record success (short UoW) ---
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
