using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Dapr-specific implementation of IJobExecutionBridge.
/// Bridges Dapr's job execution callback to Aether's JobDispatcher.
/// Looks up job entity by job name (Dapr's job identifier) and delegates to dispatcher with the handler name.
/// Extracts schema context from CloudEventEnvelope and sets it before job execution.
/// </summary>
public sealed class DaprJobExecutionBridge(
    IJobDispatcher jobDispatcher,
    IJobStore jobStore,
    ICurrentSchema currentSchema,
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
            var envelope = TryParseEnvelope(payload.ToArray());
            ReadOnlyMemory<byte> argsPayload;

            if (envelope != null)
            {
                if (!string.IsNullOrWhiteSpace(envelope.Schema))
                {
                    currentSchema.Set(envelope.Schema);
                }
                
                var argsBytes = eventSerializer.Serialize(envelope.Data);
                argsPayload = new ReadOnlyMemory<byte>(argsBytes);
            }
            else
            {
                argsPayload = payload;
            }
            
            var jobInfo = await jobStore.GetByJobNameAsync(jobName, cancellationToken);
            if (jobInfo == null)
            {
                logger.LogError("Job with name '{JobName}' not found in store", jobName);
                throw new InvalidOperationException($"Job with name '{jobName}' not found in store.");
            }
            
            // Dispatch to handler using the handler name from job entity
            await jobDispatcher.DispatchAsync(jobInfo.Id, jobInfo.HandlerName, argsPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute Dapr job '{JobName}' through execution bridge", jobName);
            throw;
        }
    }

    /// <summary>
    /// Attempts to parse the payload as a CloudEventEnvelope.
    /// Returns null if the payload is not in envelope format (old format).
    /// </summary>
    private CloudEventEnvelope? TryParseEnvelope(byte[] payload)
    {
        try
        {
            var envelope = eventSerializer.Deserialize<CloudEventEnvelope>(payload);
            
            // Validate it's actually an envelope by checking required properties
            if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Type))
            {
                return envelope;
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Payload is not in CloudEventEnvelope format, treating as legacy format");
            return null;
        }
    }
}

