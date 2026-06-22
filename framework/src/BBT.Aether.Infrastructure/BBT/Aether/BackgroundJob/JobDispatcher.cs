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

    /// <summary>
    /// A successfully claimed job (Phase 1 winner): the snapshot needed to run the handler and record the outcome.
    /// </summary>
    private readonly record struct JobClaim(
        Guid JobId,
        string HandlerName,
        JobKind Kind,
        int RetryCount,
        int MaxRetryCount,
        Guid Token);

    private async Task DispatchCoreAsync(
        AsyncServiceScope scope,
        string jobName,
        ReadOnlyMemory<byte> argsPayload,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(scope, jobName, activity, cancellationToken);
        if (claim is not { } c)
            return;

        // --- Phase 2: run the handler with NO dispatcher transaction ---
        // The dispatcher holds no open UoW/connection across the handler. The handler owns its own UoW
        // boundaries (it can open IUnitOfWorkManager.Begin around its DB work). The schema scope is active.
        try
        {
            await InvokeHandlerAsync(scope.ServiceProvider, c.HandlerName, argsPayload, cancellationToken);
            await RecordSuccessAsync(scope, c, jobName, activity, cancellationToken);
            logger.LogInformation("Successfully completed handler '{HandlerName}' for job id '{JobId}'", c.HandlerName,
                c.JobId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
            var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

            logger.LogWarning("Handler '{HandlerName}' for job id '{JobId}' was cancelled", c.HandlerName, c.JobId);
            activity?.SetTag("job.status", "cancelled");
            activity?.SetStatus(ActivityStatusCode.Ok);
            await using (var cancelUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                await jobStore.TryRecordTerminalAsync(c.JobId, c.Token, BackgroundJobStatus.Cancelled,
                    clock.UtcNow, "Job was cancelled", cancellationToken);
                await cancelUow.CommitAsync(cancellationToken);
            }
            await TryDeleteFromSchedulerAsync(jobScheduler, c.HandlerName, jobName, cancellationToken);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}".Truncate(4000)!;
            logger.LogError(ex, "Handler '{HandlerName}' for job id '{JobId}' failed", c.HandlerName, c.JobId);
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

            await RecordFailureAsync(scope, c, jobName, error, cancellationToken);
        }
    }

    /// <summary>
    /// Phase 1: single load + guards + atomic claim (one UoW). Returns the claimed job snapshot, or
    /// <c>null</c> when there is nothing to run (job missing, no handler, or claim lost to another worker).
    /// </summary>
    private async Task<JobClaim?> ClaimAsync(
        AsyncServiceScope scope,
        string jobName,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        await using var claimUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        var jobInfo = await jobStore.GetByJobNameAsync(jobName, cancellationToken);
        if (jobInfo is null)
        {
            logger.LogWarning("Job '{JobName}' not found in an active state; skipping", jobName);
            activity?.SetStatus(ActivityStatusCode.Ok);
            await claimUow.CommitAsync(cancellationToken);
            return null;
        }

        activity?.SetTag("job.id", jobInfo.Id.ToString());
        activity?.SetTag("job.handler_name", jobInfo.HandlerName);

        if (!options.Invokers.ContainsKey(jobInfo.HandlerName))
        {
            logger.LogWarning("No handler found for handler name '{HandlerName}' (job id '{JobId}')",
                jobInfo.HandlerName, jobInfo.Id);
            await jobStore.UpdateStatusAsync(jobInfo.Id, BackgroundJobStatus.Failed, clock.UtcNow,
                "No handler found for handler type", cancellationToken);
            await claimUow.CommitAsync(cancellationToken);
            await TryDeleteFromSchedulerAsync(jobScheduler, jobInfo.HandlerName, jobName, cancellationToken);
            activity?.SetTag("job.status", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, "No handler");
            return null;
        }

        // Atomic claim: only one worker wins Scheduled→Running (and stamps RunningSince + a fresh token).
        var runningToken = Guid.NewGuid();
        var claimed = await jobStore.TryClaimAsync(jobInfo.Id, clock.UtcNow, runningToken, cancellationToken);
        await claimUow.CommitAsync(cancellationToken);
        if (!claimed)
        {
            logger.LogInformation(
                "Job id '{JobId}' was not Scheduled (already claimed or late delivery); skipping", jobInfo.Id);
            activity?.SetTag("job.status", "skipped");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return null;
        }

        return new JobClaim(jobInfo.Id, jobInfo.HandlerName, jobInfo.Kind, jobInfo.RetryCount, jobInfo.MaxRetryCount,
            runningToken);
    }

    /// <summary>
    /// Phase 3 (success): records completion in a short UoW and, for one-shot jobs, removes them from the scheduler.
    /// </summary>
    private async Task RecordSuccessAsync(
        AsyncServiceScope scope,
        JobClaim claim,
        string jobName,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        bool recorded;
        await using (var doneUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            recorded = claim.Kind == JobKind.Recurring
                ? await jobStore.TryReturnToScheduledAsync(claim.JobId, claim.Token, clock.UtcNow, null, cancellationToken)
                : await jobStore.TryRecordTerminalAsync(claim.JobId, claim.Token, BackgroundJobStatus.Completed,
                    clock.UtcNow, null, cancellationToken);
            await doneUow.CommitAsync(cancellationToken);
        }

        if (!recorded)
        {
            logger.LogWarning("Claim for job id '{JobId}' was lost before success could be recorded; skipping", claim.JobId);
            activity?.SetTag("job.status", "claim-lost");
            return;
        }

        activity?.SetTag("job.status", claim.Kind == JobKind.Recurring ? "scheduled" : "completed");
        activity?.SetStatus(ActivityStatusCode.Ok);

        if (claim.Kind == JobKind.OneShot)
            await TryDeleteFromSchedulerAsync(jobScheduler, claim.HandlerName, jobName, cancellationToken); // recurring stays armed
    }

    /// <summary>
    /// Phase 3 (failure): records the error in a short UoW (recurring → Scheduled, one-shot → Retrying or Failed)
    /// and removes a terminally-failed one-shot from the scheduler.
    /// </summary>
    private async Task RecordFailureAsync(
        AsyncServiceScope scope,
        JobClaim claim,
        string jobName,
        string error,
        CancellationToken cancellationToken)
    {
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        var willRetry = claim.Kind == JobKind.OneShot && claim.RetryCount + 1 <= claim.MaxRetryCount;

        bool recorded;
        await using (var failUow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
        {
            if (claim.Kind == JobKind.Recurring)
            {
                // Recurring jobs rely on the next cron occurrence; record the error, return to Scheduled.
                recorded = await jobStore.TryReturnToScheduledAsync(claim.JobId, claim.Token, clock.UtcNow, error, cancellationToken);
            }
            else if (willRetry)
            {
                var nextRetryAt = clock.UtcNow + ComputeBackoff(claim.RetryCount, options);
                recorded = await jobStore.TryMarkRetryingAsync(claim.JobId, claim.Token, nextRetryAt, error, cancellationToken); // → Retrying; poller re-arms
            }
            else
            {
                recorded = await jobStore.TryRecordTerminalAsync(claim.JobId, claim.Token, BackgroundJobStatus.Failed,
                    clock.UtcNow, error, cancellationToken);
            }

            await failUow.CommitAsync(cancellationToken);
        }

        if (!recorded)
        {
            logger.LogWarning("Claim for job id '{JobId}' was lost before failure could be recorded; skipping", claim.JobId);
            return;
        }

        // One-shot terminal (Failed) and recurring stay armed in Dapr; a Retrying one-shot is re-armed by the
        // poller, so only delete from the scheduler when the one-shot is terminally Failed.
        if (claim.Kind == JobKind.OneShot && !willRetry)
            await TryDeleteFromSchedulerAsync(jobScheduler, claim.HandlerName, jobName, cancellationToken);
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
