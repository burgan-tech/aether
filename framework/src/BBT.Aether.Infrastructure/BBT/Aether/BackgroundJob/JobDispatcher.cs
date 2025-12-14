using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
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

        await using var scope = scopeFactory.CreateAsyncScope();
        
        var argsPayload = CloudEventEnvelopeHelper.ExtractDataPayload(eventSerializer, jobPayload, out var envelope);

        if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Schema))
        {
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            currentSchema.Set(envelope.Schema);
        }

        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();

        // First UoW: Check idempotency and update status to Running
        await using (var uow = await uowManager.BeginRequiresNew(cancellationToken))
        {
            if (await IsJobAlreadyProcessedAsync(jobStore, jobId, handlerName, cancellationToken))
            {
                await uow.CommitAsync(cancellationToken);
                return;
            }

            if (!options.Invokers.ContainsKey(handlerName))
            {
                logger.LogWarning("No handler found for handler name '{HandlerName}' with job id '{JobId}'", handlerName,
                    jobId);
                await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Failed, clock.UtcNow,
                    "No handler found for handler type", cancellationToken);
                await uow.CommitAsync(cancellationToken);
                return;
            }

            // Update status to Running
            await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Running,
                cancellationToken: cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }

        // Second UoW: Execute handler and mark as completed
        try
        {
            await using var handlerUow = await uowManager.BeginRequiresNew(cancellationToken);

            await InvokeHandlerAsync(scope.ServiceProvider, handlerName, argsPayload, cancellationToken);

            // Update status to Completed
            await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Completed,
                clock.UtcNow, cancellationToken: cancellationToken);
            logger.LogInformation("Successfully completed handler '{HandlerName}' for job id '{JobId}'", handlerName,
                jobId);

            await handlerUow.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Handler '{HandlerName}' for job id '{JobId}' was cancelled", handlerName, jobId);
            await MarkJobStatusAsync(uowManager, jobStore, jobId, BackgroundJobStatus.Cancelled,
                "Job was cancelled", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handler '{HandlerName}' for job id '{JobId}' failed", handlerName, jobId);
            var errorMessage = $"{ex.GetType().Name}: {ex.Message}".Truncate(4000);
            await MarkJobStatusAsync(uowManager, jobStore, jobId, BackgroundJobStatus.Failed,
                errorMessage, cancellationToken);
        }
    }

    /// <summary>
    /// Checks if a job has already been processed (idempotency check).
    /// Returns true if job is in Completed or Cancelled state.
    /// </summary>
    private async Task<bool> IsJobAlreadyProcessedAsync(IJobStore jobStore, Guid jobId, string handlerName,
        CancellationToken cancellationToken)
    {
        var jobInfo = await jobStore.GetAsync(jobId, cancellationToken);
        if (jobInfo == null)
            return false;

        // If job is already completed or cancelled, skip reprocessing
        if (jobInfo.Status == BackgroundJobStatus.Completed)
        {
            logger.LogWarning(
                "Handler '{HandlerName}' for job id '{JobId}' already completed. Skipping reprocessing (idempotency).",
                handlerName, jobId);
            return true;
        }

        if (jobInfo.Status == BackgroundJobStatus.Cancelled)
        {
            logger.LogWarning(
                "Handler '{HandlerName}' for job id '{JobId}' was cancelled. Skipping reprocessing (idempotency).",
                handlerName, jobId);
            return true;
        }

        return false;
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
            await using var uow = await uowManager.BeginRequiresNew(cancellationToken);
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