using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Dapr;

/// <summary>
/// Dapr-specific implementation of IJobExecutionBridge.
/// Bridges Dapr's job execution callback to Aether's JobDispatcher.
/// Extracts the CloudEventEnvelope, sets the schema scope for multi-tenant support,
/// and dispatches by job name. The dispatcher resolves and atomically claims the job itself,
/// so the bridge performs no database work.
/// </summary>
public sealed class DaprJobExecutionBridge(
    IServiceScopeFactory scopeFactory,
    IEventSerializer eventSerializer,
    ILogger<DaprJobExecutionBridge> logger)
    : IJobExecutionBridge
{
    public async Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "BackgroundJob.Execute",
            ActivityKind.Consumer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("job.scheduler", "dapr");
        activity?.SetTag("job.name", jobName);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Parse envelope and set schema context (multi-tenant support) before dispatch.
            var dataPayload = CloudEventEnvelopeHelper.ExtractDataPayload(eventSerializer, payload, out var envelope);

            IDisposable? schemaScope = null;
            if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Schema))
            {
                var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
                schemaScope = currentSchema.Change(envelope.Schema);
            }

            using (schemaScope)
            {
                // Dispatch by job name with the extracted data payload. The dispatcher re-resolves the job,
                // atomically claims it, runs the handler with no held transaction, and records the outcome.
                var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
                await dispatcher.DispatchAsync(jobName, dataPayload, cancellationToken);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute Dapr job '{JobName}' through execution bridge", jobName);

            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
                    { "exception.message", ex.Message },
                }));
            }

            throw;
        }
    }
}
