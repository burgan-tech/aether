using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Dapr-specific implementation of IJobExecutionBridge.
/// Bridges Dapr's job execution callback to Aether's JobDispatcher.
/// Looks up job entity by job name (Dapr's job identifier) and delegates to dispatcher with the handler name.
/// </summary>
public sealed class DaprJobExecutionBridge(
    IJobDispatcher jobDispatcher,
    IJobStore jobStore,
    ILogger<DaprJobExecutionBridge> logger)
    : IJobExecutionBridge
{
    public async Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        try
        {
            // Lookup job entity by job name (Dapr's unique identifier)
            var jobInfo = await jobStore.GetByJobNameAsync(jobName, cancellationToken);
            if (jobInfo == null)
            {
                logger.LogError("Job with name '{JobName}' not found in store", jobName);
                throw new InvalidOperationException($"Job with name '{jobName}' not found in store.");
            }

            // Dispatch to handler using the handler name from job entity
            await jobDispatcher.DispatchAsync(jobInfo.Id, jobInfo.HandlerName, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute Dapr job '{JobName}' through execution bridge", jobName);
            throw;
        }
    }
}

