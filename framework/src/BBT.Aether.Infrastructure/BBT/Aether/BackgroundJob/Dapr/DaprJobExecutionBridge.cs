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

            // Parse envelope and set schema context (multi-tenant support) before dispatch. The schema
            // must come off the wire: it decides which schema the dispatcher's own job-row read runs in,
            // so it cannot be recovered from the row itself.
            var envelope = CloudEventEnvelopeHelper.TryParseEnvelope(eventSerializer, payload.ToArray());

            IDisposable? schemaScope = null;
            if (envelope != null && !string.IsNullOrWhiteSpace(envelope.Schema))
            {
                var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
                schemaScope = currentSchema.Change(envelope.Schema);
            }

            using (schemaScope)
            {
                // Hand the dispatcher the envelope as received, NOT the extracted data. An armed message
                // is a reference (header only, no `data`), and the dispatcher needs the extension
                // attributes to recognise that and rehydrate the arguments from the job row. It performs
                // the same extraction itself for inline and legacy payloads, so this is lossless.
                var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
                await dispatcher.DispatchAsync(jobName, payload, cancellationToken);

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
