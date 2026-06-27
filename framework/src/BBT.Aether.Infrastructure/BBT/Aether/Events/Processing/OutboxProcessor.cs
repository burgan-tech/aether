using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Processor that processes outbox messages using a lease-based 3-phase approach.
/// Phase 1: lease messages (short transaction). Phase 2: publish (no transaction). Phase 3: write outcomes (short transaction).
/// </summary>
public class OutboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity workerIdentity,
    IClock clock,
    ILogger<OutboxProcessor<TDbContext>> logger,
    AetherOutboxOptions options) : IOutboxProcessor
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    /// <inheritdoc />
    public virtual async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var processed = await ProcessOutboxMessagesAsync(cancellationToken);
            await CleanupProcessedMessagesAsync(cancellationToken);
            return processed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing outbox messages");
            return 0;
        }
    }

    /// <summary>
    /// Leases a batch, publishes each message, then persists outcomes.
    /// </summary>
    protected virtual async Task<int> ProcessOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Outbox processor has no Schema configured; skipping run.");
            return 0;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var eventBus = sp.GetRequiredService<IDistributedEventBus>();
        var eventBusOptions = sp.GetRequiredService<AetherEventBusOptions>();
        var leaseStore = sp.GetRequiredService<IOutboxLeaseStore>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();

        var workerId = $"{workerIdentity.Value}/outbox";
        var partitionLeaseStore = sp.GetRequiredService<IPartitionLeaseStore>();

        // PARTITION LEASE: acquire or renew partition ownership when partitioning is enabled.
        IReadOnlyList<int>? ownedPartitions = null;
        if (options.PartitioningEnabled)
        {
            await using var partitionScope = scopeFactory.CreateAsyncScope();
            var partitionUowManager = partitionScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var partitionCurrentSchema = partitionScope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            using (partitionCurrentSchema.Change(options.Schema))
            {
                await using var partitionUow = partitionUowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });
                ownedPartitions = await partitionLeaseStore.AcquireOrRenewAsync(
                    "outbox", workerId, options.MaxOwnedPartitions, options.PartitionLeaseDuration, cancellationToken);
                await partitionUow.CommitAsync(cancellationToken);
            }

            if (ownedPartitions.Count == 0)
            {
                logger.LogDebug("Outbox worker {WorkerId} owns no partitions; skipping run.", workerId);
                return 0;
            }
        }

        using (currentSchema.Change(options.Schema))
        {
            // PHASE 1: lease — kısa transaction
            List<OutboxMessage> messages;
            await using (var leaseUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                messages = (await leaseStore.LeaseBatchAsync(
                    options.BatchSize, workerId, options.LeaseDuration, ownedPartitions, cancellationToken)).ToList();
                await leaseUow.CommitAsync(cancellationToken);
            }

            if (messages.Count == 0) return 0;

            logger.LogInformation("Leased {Count} outbox messages for worker {WorkerId}", messages.Count, workerId);

            // PHASE 2: publish — transaction açık değil
            var outcomes = new List<OutboxPublishOutcome>(messages.Count);
            foreach (var message in messages)
            {
                if (cancellationToken.IsCancellationRequested) break;

                using var activity = InfrastructureActivitySource.Source.StartActivity(
                    "Outbox.Process", ActivityKind.Producer, Activity.Current?.Context ?? default);

                var topicName = message.ExtraProperties.TryGetValue("TopicName", out var topicObj)
                    ? topicObj?.ToString() ?? message.EventName : message.EventName;
                var pubSubName = message.ExtraProperties.TryGetValue("PubSubName", out var pubSubObj)
                    ? pubSubObj?.ToString() ?? eventBusOptions.PubSubName : eventBusOptions.PubSubName;

                activity?.SetTag("event.name", message.EventName);
                activity?.SetTag("event.topic", topicName);
                activity?.SetTag("outbox.message_id", message.Id.ToString());
                activity?.SetTag("outbox.retry_count", message.RetryCount);

                try
                {
                    await eventBus.PublishEnvelopeAsync(message.EventData, topicName, pubSubName, cancellationToken);
                    outcomes.Add(new OutboxPublishOutcome(message.Id, true, null));
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    logger.LogInformation("Published outbox message {MessageId}", message.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);
                    RecordException(activity, ex);
                    outcomes.Add(new OutboxPublishOutcome(message.Id, false, ex.Message));
                }
            }

            if (outcomes.Count == 0) return 0;

            // PHASE 3: outcome yaz — kısa transaction, locked_by guard
            await using (var updateUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
                var now = clock.UtcNow;

                foreach (var outcome in outcomes)
                {
                    if (outcome.Success)
                    {
                        // LockedBy guard: sadece bu worker'ın hâlâ sahip olduğu mesajları güncelle
                        var affected = await dbContext.OutboxMessages
                            .Where(m => m.Id == outcome.MessageId
                                     && m.LockedBy == workerId
                                     && m.LockedUntil > now)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(m => m.Status, OutboxMessageStatus.Processed)
                                .SetProperty(m => m.ProcessedAt, now)
                                .SetProperty(m => m.LockedBy, (string?)null)
                                .SetProperty(m => m.LockedUntil, (DateTime?)null),
                                cancellationToken);

                        if (affected == 0)
                            logger.LogWarning("Outbox message {MessageId} lease expired or taken by another worker; skipping outcome write", outcome.MessageId);
                    }
                    else
                    {
                        var domainMessage = await dbContext.OutboxMessages
                            .Where(m => m.Id == outcome.MessageId && m.LockedBy == workerId)
                            .FirstOrDefaultAsync(cancellationToken);

                        if (domainMessage == null) continue;

                        if (domainMessage.RetryCount + 1 >= options.MaxRetryCount)
                        {
                            domainMessage.Status = OutboxMessageStatus.DeadLetter;
                        }
                        else
                        {
                            domainMessage.RetryCount++;
                            domainMessage.LastError = outcome.Error?.Length > 4000
                                ? outcome.Error[..4000] : outcome.Error;
                            domainMessage.NextRetryAt = CalculateNextRetryTime(domainMessage.RetryCount);
                            domainMessage.Status = OutboxMessageStatus.Pending;
                        }

                        domainMessage.LockedBy = null;
                        domainMessage.LockedUntil = null;
                    }
                }

                await updateUow.CommitAsync(cancellationToken);
            }

            return outcomes.Count;
        }
    }

    private readonly record struct OutboxPublishOutcome(Guid MessageId, bool Success, string? Error);

    /// <summary>
    /// Deletes processed messages older than the configured retention period.
    /// </summary>
    protected virtual async Task CleanupProcessedMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema)) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();

        using (currentSchema.Change(options.Schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
            var cutoffDate = clock.UtcNow - options.RetentionPeriod;

            var processed = await dbContext.OutboxMessages
                .Where(m => m.Status == OutboxMessageStatus.Processed
                         && m.ProcessedAt != null
                         && m.ProcessedAt < cutoffDate)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken);

            if (processed.Count > 0)
            {
                logger.LogInformation("Cleaning up {Count} processed outbox messages", processed.Count);
                dbContext.OutboxMessages.RemoveRange(processed);
            }

            await uow.CommitAsync(cancellationToken);
        }
    }

    private DateTime CalculateNextRetryTime(int retryCount)
    {
        var delay = options.RetryBaseDelay * Math.Pow(2, retryCount - 1);
        return clock.UtcNow.Add(TimeSpan.FromMilliseconds(delay.TotalMilliseconds));
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
