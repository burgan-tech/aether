using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Processor that handles pending inbox messages with distributed lock coordination.
/// Polls for pending events, processes them using registered handlers, and cleans up old processed messages.
/// </summary>
public class InboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IDistributedLockService distributedLockService,
    ILogger<InboxProcessor<TDbContext>> logger,
    AetherInboxOptions options) : IInboxProcessor
    where TDbContext : DbContext, IHasEfCoreInbox
{
    private readonly string _workerId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <inheritdoc />
    public virtual async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ProcessWithLockAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in inbox processing cycle");
        }
    }

    protected virtual async Task ProcessWithLockAsync(CancellationToken cancellationToken)
    {
        // Process pending events using database-level locking (no distributed lock needed)
        await ProcessPendingEventsAsync(cancellationToken);

        // Cleanup uses distributed lock to ensure only one processor cleans at a time
        await CleanupOldMessagesWithLockAsync(cancellationToken);
    }

    protected virtual async Task ProcessPendingEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Create a new scope for each batch
            await using var scope = scopeFactory.CreateAsyncScope();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            // Lease pending events with database-level locking
            var pendingEvents = await inboxStore.LeaseBatchAsync(
                options.ProcessingBatchSize,
                _workerId,
                options.LeaseDuration,
                cancellationToken);

            if (!pendingEvents.Any())
            {
                break;
            }

            logger.LogInformation("Leased {Count} pending events in the inbox for worker {WorkerId}", 
                pendingEvents.Count, _workerId);

            foreach (var inboxMessage in pendingEvents)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await ProcessSingleEventAsync(inboxMessage, scope.ServiceProvider, cancellationToken);
            }
        }
    }

    private async Task ProcessSingleEventAsync(
        InboxMessage inboxMessage,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Start processing incoming event with id = {EventId}", inboxMessage.Id);

        try
        {
            // Begin a new UoW for this event processing
            var unitOfWorkManager = scopedServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var uow = await unitOfWorkManager.BeginRequiresNew(cancellationToken);

            var inboxStore = scopedServiceProvider.GetRequiredService<IInboxStore>();
            var invokerRegistry = scopedServiceProvider.GetRequiredService<IDistributedEventInvokerRegistry>();
            var eventSerializer = scopedServiceProvider.GetRequiredService<IEventSerializer>();

            // Mark as processing
            await inboxStore.MarkAsProcessingAsync(inboxMessage.Id, cancellationToken);
            await uow.CommitAsync(cancellationToken);

            // Deserialize envelope to get event name
            var envelope = eventSerializer.Deserialize<CloudEventEnvelope>(inboxMessage.EventData);
            if (envelope == null)
            {
                logger.LogWarning("Failed to deserialize event {EventId}, marking as failed", inboxMessage.Id);
                await MarkEventAsFailedAsync(inboxMessage.Id, scopedServiceProvider, cancellationToken);
                return;
            }

            var eventName = envelope.Type;
            var version = envelope.Version ?? 1;

            // Lookup invoker from registry
            if (!invokerRegistry.TryGet(eventName, version, out var invoker))
            {
                logger.LogWarning("No handler registered for event {EventName} v{Version}, marking as failed",
                    eventName, version);
                await MarkEventAsFailedAsync(inboxMessage.Id, scopedServiceProvider, cancellationToken);
                return;
            }

            // Begin a new UoW for handler execution + marking as processed
            await using var handlerUow = await unitOfWorkManager.BeginRequiresNew(cancellationToken);

            // Invoke the handler
            await invoker.InvokeAsync(scopedServiceProvider, inboxMessage.EventData, cancellationToken);

            // Mark as processed
            await inboxStore.MarkAsProcessedAsync(inboxMessage.Id, cancellationToken);
            // Commit handler changes + processed status
            await handlerUow.CommitAsync(cancellationToken);

            logger.LogInformation("Successfully processed event {EventId} ({EventName} v{Version})",
                inboxMessage.Id, eventName, version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process event {EventId}", inboxMessage.Id);
            await MarkEventAsFailedAsync(inboxMessage.Id, scopedServiceProvider, cancellationToken);
        }
    }

    private async Task MarkEventAsFailedAsync(
        string eventId,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var unitOfWorkManager = scopedServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var uow = await unitOfWorkManager.BeginRequiresNew(cancellationToken);

            var inboxStore = scopedServiceProvider.GetRequiredService<IInboxStore>();
            await inboxStore.MarkAsFailedAsync(eventId, cancellationToken);

            await uow.CommitAsync(cancellationToken);

            logger.LogInformation("Event {EventId} marked as discarded", eventId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark event {EventId} as failed", eventId);
        }
    }

    protected virtual async Task CleanupOldMessagesWithLockAsync(CancellationToken cancellationToken)
    {
        // Use distributed lock for cleanup to avoid concurrent cleanup operations
        await using var lockHandle = await distributedLockService.TryAcquireLockAsync(
            options.DistributedLockName + ":cleanup",
            options.LockExpirySeconds,
            cancellationToken);

        if (lockHandle == null)
        {
            logger.LogDebug("Could not acquire distributed lock for cleanup");
            return;
        }

        await CleanupOldMessagesAsync(cancellationToken);
    }

    protected virtual async Task CleanupOldMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            var deletedCount = await inboxStore.CleanupOldMessagesAsync(
                options.CleanupBatchSize,
                options.RetentionPeriod,
                cancellationToken);

            if (deletedCount > 0)
            {
                logger.LogInformation("Cleaned up {Count} old inbox messages", deletedCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cleaning up old inbox messages");
        }
    }
}