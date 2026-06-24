using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public class InboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity workerIdentity,
    ILogger<InboxProcessor<TDbContext>> logger,
    AetherInboxOptions options) : IInboxProcessor
    where TDbContext : DbContext, IHasEfCoreInbox
{
    public virtual async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var processed = await ProcessPendingEventsAsync(cancellationToken);
            await CleanupOldMessagesAsync(cancellationToken);
            return processed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in inbox processing cycle");
            return 0;
        }
    }

    protected virtual async Task<int> ProcessPendingEventsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Inbox processor has no Schema configured; skipping run.");
            return 0;
        }

        var totalProcessed = 0;
        var workerId = $"{workerIdentity.Value}/inbox";

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

            using (currentSchema.Change(options.Schema!))
            {
                IReadOnlyList<InboxMessage> pendingEvents;
                await using (var leaseUow = unitOfWorkManager.BeginRequiresNew())
                {
                    pendingEvents = await leaseStore.LeaseBatchAsync(
                        options.ProcessingBatchSize, workerId, options.LeaseDuration, cancellationToken);
                    await leaseUow.CommitAsync(cancellationToken);
                }

                if (!pendingEvents.Any()) break;

                logger.LogInformation("Leased {Count} inbox events for worker {WorkerId}", pendingEvents.Count, workerId);

                foreach (var inboxMessage in pendingEvents)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await ProcessSingleEventAsync(inboxMessage, scope.ServiceProvider, cancellationToken);
                    totalProcessed++;
                }
            }
        }

        return totalProcessed;
    }

    private async Task ProcessSingleEventAsync(
        InboxMessage inboxMessage,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "Inbox.Process", ActivityKind.Consumer, Activity.Current?.Context ?? default);

        activity?.SetTag("event.id", inboxMessage.Id);
        logger.LogInformation("Processing inbox event {EventId}", inboxMessage.Id);

        try
        {
            var unitOfWorkManager = scopedServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var inboxStore = scopedServiceProvider.GetRequiredService<IInboxStore>();
            var invokerRegistry = scopedServiceProvider.GetRequiredService<IDistributedEventInvokerRegistry>();
            var eventSerializer = scopedServiceProvider.GetRequiredService<IEventSerializer>();

            var envelope = eventSerializer.Deserialize<CloudEventEnvelope>(inboxMessage.EventData);
            if (envelope == null)
            {
                logger.LogWarning("Failed to deserialize event {EventId}", inboxMessage.Id);
                await MarkFailedAsync(inboxMessage.Id, inboxStore, unitOfWorkManager, cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Error, "Deserialization failed");
                return;
            }

            var eventName = envelope.Type;
            var version = envelope.Version ?? 1;
            activity?.SetTag("event.name", eventName);
            activity?.SetTag("event.version", version);

            if (!invokerRegistry.TryGet(eventName, version, out var invoker))
            {
                logger.LogWarning("No handler for {EventName} v{Version}", eventName, version);
                await MarkFailedAsync(inboxMessage.Id, inboxStore, unitOfWorkManager, cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Error, $"No handler for {eventName} v{version}");
                return;
            }

            await using var handlerUow = unitOfWorkManager.BeginRequiresNew();
            await invoker.InvokeAsync(scopedServiceProvider, inboxMessage.EventData, cancellationToken);
            await inboxStore.MarkAsProcessedAsync(inboxMessage.Id, cancellationToken);
            await handlerUow.CommitAsync(cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.LogInformation("Processed event {EventId} ({EventName} v{Version})", inboxMessage.Id, eventName, version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process event {EventId}", inboxMessage.Id);
            RecordException(activity, ex);
            var uowManager = scopedServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var store = scopedServiceProvider.GetRequiredService<IInboxStore>();
            await MarkFailedAsync(inboxMessage.Id, store, uowManager, cancellationToken);
        }
    }

    private static async Task MarkFailedAsync(
        string eventId,
        IInboxStore inboxStore,
        IUnitOfWorkManager uowManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var uow = uowManager.BeginRequiresNew();
            await inboxStore.MarkAsFailedAsync(eventId, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _ = ex; // batch devam etmeli
        }
    }

    protected virtual async Task CleanupOldMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema)) return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            using (currentSchema.Change(options.Schema!))
            {
                await using var uow = unitOfWorkManager.BeginRequiresNew();
                var deletedCount = await inboxStore.CleanupOldMessagesAsync(
                    options.CleanupBatchSize, options.RetentionPeriod, cancellationToken);
                await uow.CommitAsync(cancellationToken);

                if (deletedCount > 0)
                    logger.LogInformation("Cleaned up {Count} old inbox messages", deletedCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cleaning up old inbox messages");
        }
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }
}
