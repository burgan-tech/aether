using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob;

/// <inheritdoc />
public class JobDispatcher(
    IServiceScopeFactory scopeFactory,
    IJobStore jobStore,
    IUnitOfWorkManager uowManager,
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
        
        if (await IsJobAlreadyProcessedAsync(jobId, handlerName, cancellationToken))
            return;
        
        if (!options.Invokers.ContainsKey(handlerName))
        {
            logger.LogWarning("No handler found for handler name '{HandlerName}' with job id '{JobId}'", handlerName, jobId);
            await UpdateStatusWithinUowAsync(jobId, BackgroundJobStatus.Failed,
                "No handler found for handler type", cancellationToken);
            return;
        }
        
        try
        {
            // Update status to Running
            await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Running,
                cancellationToken: cancellationToken);

            await InvokeHandlerAsync(handlerName, jobPayload, cancellationToken);

            // Update status to Completed
            await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Completed,
                clock.UtcNow, cancellationToken: cancellationToken);

            logger.LogInformation("Successfully completed handler '{HandlerName}' for job id '{JobId}'", handlerName, jobId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Handler '{HandlerName}' for job id '{JobId}' was cancelled", handlerName, jobId);
            await HandleJobCancellationAsync(jobId, cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handler '{HandlerName}' for job id '{JobId}' failed", handlerName, jobId);
            await HandleJobFailureAsync(jobId, ex, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Checks if a job has already been processed (idempotency check).
    /// Returns true if job is in Completed or Cancelled state.
    /// </summary>
    private async Task<bool> IsJobAlreadyProcessedAsync(Guid jobId, string handlerName, CancellationToken cancellationToken)
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
            logger.LogWarning("Handler '{HandlerName}' for job id '{JobId}' was cancelled. Skipping reprocessing (idempotency).",
                handlerName, jobId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates job status within a UoW transaction.
    /// Helper method to reduce code duplication.
    /// </summary>
    private async Task UpdateStatusWithinUowAsync(
        Guid jobId,
        BackgroundJobStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await using var uow = await uowManager.BeginAsync(cancellationToken: cancellationToken);
        try
        {
            await jobStore.UpdateStatusAsync(jobId, status, clock.UtcNow, error, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update job status to {Status}", status);
            await uow.RollbackAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Handles job cancellation by updating status within the existing UoW.
    /// </summary>
    private async Task HandleJobCancellationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Cancelled,
                clock.UtcNow, "Job was cancelled", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update job status to Cancelled");
        }
    }

    /// <summary>
    /// Handles job failure by updating status within the existing UoW.
    /// </summary>
    private async Task HandleJobFailureAsync(Guid jobId, Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var errorMessage = $"{exception.GetType().Name}: {exception.Message}";
            errorMessage = errorMessage.Truncate(4000);

            await jobStore.UpdateStatusAsync(jobId, BackgroundJobStatus.Failed,
                clock.UtcNow, errorMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update job status to Failed");
        }
    }

    /// <summary>
    /// Invokes the handler using pre-created invoker (no runtime reflection).
    /// Generic type TArgs was closed at registration time (startup), not at runtime.
    /// This method is completely type-safe and fast.
    /// </summary>
    private async Task InvokeHandlerAsync(string handlerName, ReadOnlyMemory<byte> jobPayload,
        CancellationToken cancellationToken)
    {
        // Get pre-created invoker from options (generic already closed at startup)
        if (!options.Invokers.TryGetValue(handlerName, out var invoker))
        {
            throw new InvalidOperationException($"No invoker registered for handler '{handlerName}'");
        }

        // Invoke handler - completely type-safe, no reflection at runtime
        await invoker.InvokeAsync(scopeFactory, eventSerializer, jobPayload, cancellationToken);
    }
}