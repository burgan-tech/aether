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
/// Processor that processes outbox messages.
/// Retries failed messages with exponential backoff and cleans up old processed messages.
/// </summary>
public class OutboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<OutboxProcessor<TDbContext>> logger,
    AetherOutboxOptions options) : IOutboxProcessor
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    private readonly string _workerId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <inheritdoc />
    public virtual async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ProcessOutboxMessagesAsync(cancellationToken);
            await CleanupProcessedMessagesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing outbox messages");
        }
    }

    protected virtual async Task ProcessOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Outbox processor has no Schema configured; skipping run.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var eventBus = sp.GetRequiredService<IDistributedEventBus>();
        var eventBusOptions = sp.GetRequiredService<AetherEventBusOptions>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();

        using (currentSchema.Change(options.Schema))
        {
            // PHASE 1: lease messages with database-level locking (short transaction).
            // The raw-SQL row locks are released when this UoW commits, but the soft lease
            // columns (LockedBy/LockedUntil) persist, so other workers still skip these
            // messages until the lease expires.
            List<OutboxMessage> messages;
            await using (var leaseUow = await uowManager.BeginAsync(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true },
                cancellationToken))
            {
                messages = (await outboxStore.LeaseBatchAsync(
                    options.BatchSize,
                    _workerId,
                    options.LeaseDuration,
                    cancellationToken)).ToList();

                await leaseUow.CommitAsync(cancellationToken);
            }

            if (messages.Count == 0)
            {
                return;
            }

            logger.LogInformation("Leased {Count} outbox messages for processing by worker {WorkerId}",
                messages.Count, _workerId);

            // PHASE 2: publish each message WITHOUT an open transaction, recording the outcome.
            var outcomes = new List<OutboxPublishOutcome>(messages.Count);

            foreach (var message in messages)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using var activity = InfrastructureActivitySource.Source.StartActivity(
                    "Outbox.Process",
                    ActivityKind.Producer,
                    Activity.Current?.Context ?? default);

                // Get topicName and pubSubName from ExtraProperties
                var topicName = message.ExtraProperties.TryGetValue("TopicName", out var topicObj)
                    ? topicObj?.ToString() ?? message.EventName
                    : message.EventName;
                var pubSubName = message.ExtraProperties.TryGetValue("PubSubName", out var pubSubObj)
                    ? pubSubObj?.ToString() ?? eventBusOptions.PubSubName
                    : eventBusOptions.PubSubName;

                activity?.SetTag("event.name", message.EventName);
                activity?.SetTag("event.topic", topicName);
                activity?.SetTag("event.pubsub_name", pubSubName);
                activity?.SetTag("outbox.message_id", message.Id.ToString());
                activity?.SetTag("outbox.retry_count", message.RetryCount);

                try
                {
                    var serializedEnvelope = message.EventData;
                    await eventBus.PublishEnvelopeAsync(serializedEnvelope, topicName, pubSubName, cancellationToken);

                    outcomes.Add(new OutboxPublishOutcome(message.Id, true, null));

                    activity?.SetStatus(ActivityStatusCode.Ok);
                    logger.LogInformation("Successfully published outbox message {MessageId}", message.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);
                    RecordException(activity, ex);

                    outcomes.Add(new OutboxPublishOutcome(message.Id, false, ex.Message));
                }
            }

            if (outcomes.Count == 0)
            {
                return;
            }

            // PHASE 3: persist status updates (short transaction). No external calls here.
            await using (var updateUow = await uowManager.BeginAsync(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true },
                cancellationToken))
            {
                var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

                foreach (var outcome in outcomes)
                {
                    var domainMessage = await dbContext.OutboxMessages.FindAsync(new object[] { outcome.MessageId }, cancellationToken);
                    if (domainMessage == null)
                    {
                        continue;
                    }

                    if (outcome.Success)
                    {
                        domainMessage.Status = OutboxMessageStatus.Processed;
                        domainMessage.ProcessedAt = clock.UtcNow;
                        domainMessage.LastError = null;
                        domainMessage.LockedBy = null;
                        domainMessage.LockedUntil = null;
                    }
                    else
                    {
                        domainMessage.RetryCount++;
                        domainMessage.LastError = outcome.Error != null && outcome.Error.Length > 4000
                            ? outcome.Error.Substring(0, 4000)
                            : outcome.Error;
                        domainMessage.NextRetryAt = CalculateNextRetryTime(domainMessage.RetryCount);
                        domainMessage.Status = OutboxMessageStatus.Pending;
                        domainMessage.LockedBy = null;
                        domainMessage.LockedUntil = null;
                    }
                }

                await updateUow.CommitAsync(cancellationToken);
            }
        }
    }

    private readonly record struct OutboxPublishOutcome(Guid MessageId, bool Success, string? Error);

    protected virtual async Task CleanupProcessedMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Outbox processor has no Schema configured; skipping cleanup.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();

        using (currentSchema.Change(options.Schema))
        {
            await using var uow = await uowManager.BeginAsync(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true },
                cancellationToken);

            var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

            var cutoffDate = clock.UtcNow - options.RetentionPeriod;

            var processedMessages = await dbContext.OutboxMessages
                .Where(m => m.Status == OutboxMessageStatus.Processed && m.ProcessedAt != null && m.ProcessedAt < cutoffDate)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken);

            if (processedMessages.Any())
            {
                logger.LogInformation("Cleaning up {Count} processed outbox messages", processedMessages.Count);
                dbContext.OutboxMessages.RemoveRange(processedMessages);
            }

            await uow.CommitAsync(cancellationToken);
        }
    }

    private DateTime CalculateNextRetryTime(int retryCount)
    {
        // Exponential backoff: baseDelay * 2^(retryCount - 1)
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
