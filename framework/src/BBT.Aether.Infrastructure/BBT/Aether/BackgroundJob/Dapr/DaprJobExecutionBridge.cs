using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Dapr-specific implementation of IJobExecutionBridge.
/// Bridges Dapr's job execution callback to Aether's JobDispatcher.
/// Looks up job entity by job name (Dapr's job identifier) and delegates to dispatcher.
/// Extracts schema context from CloudEventEnvelope before jobStore access for multi-tenant support.
/// </summary>
public sealed class DaprJobExecutionBridge(
    IServiceScopeFactory scopeFactory,
    IJobDispatcher jobDispatcher,
    IEventSerializer eventSerializer,
    ILogger<DaprJobExecutionBridge> logger)
    : IJobExecutionBridge
{
    public async Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Parse envelope and set schema context before jobStore access (multi-tenant support)
            var dataPayload = CloudEventEnvelopeHelper.ExtractDataPayload(eventSerializer, payload, out var envelope);

            if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Schema))
            {
                var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
                currentSchema.Set(envelope.Schema);
            }

            var jobStore = scope.ServiceProvider.GetRequiredService<IJobStore>();
            var jobInfo = await jobStore.GetByJobNameAsync(jobName, cancellationToken);

            if (jobInfo == null)
            {
                logger.LogError("Job with name '{JobName}' not found in store", jobName);
                return;
            }

            // Dispatch to handler with extracted data payload
            await jobDispatcher.DispatchAsync(jobInfo.Id, jobInfo.HandlerName, dataPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute Dapr job '{JobName}' through execution bridge", jobName);
            throw;
        }
    }
}