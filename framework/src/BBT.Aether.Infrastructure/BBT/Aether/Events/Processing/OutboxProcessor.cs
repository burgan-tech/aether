using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IDistributedEventBus>();
        var eventBusOptions = scope.ServiceProvider.GetRequiredService<AetherEventBusOptions>();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

        // Lease messages with database-level locking
        var messages = await outboxStore.LeaseBatchAsync(
            options.BatchSize,
            _workerId,
            options.LeaseDuration,
            cancellationToken);

        if (!messages.Any())
        {
            return;
        }

        logger.LogInformation("Leased {Count} outbox messages for processing by worker {WorkerId}", 
            messages.Count, _workerId);

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            
            try
            {
                // Get topicName and pubSubName from ExtraProperties
                var topicName = message.ExtraProperties.TryGetValue("TopicName", out var topicObj) 
                    ? topicObj?.ToString() ?? message.EventName 
                    : message.EventName;
                var pubSubName = message.ExtraProperties.TryGetValue("PubSubName", out var pubSubObj)
                    ? pubSubObj?.ToString() ?? eventBusOptions.PubSubName
                    : eventBusOptions.PubSubName;

                // EventData is already serialized CloudEventEnvelope bytes
                var serializedEnvelope = message.EventData;

                // Publish using IDistributedEventBus abstraction
                await eventBus.PublishEnvelopeAsync(serializedEnvelope, topicName, pubSubName, cancellationToken);

                // Find and update the domain entity
                var domainMessage = await dbContext.OutboxMessages.FindAsync(new object[] { message.Id }, cancellationToken);
                if (domainMessage != null)
                {
                    domainMessage.Status = OutboxMessageStatus.Processed;
                    domainMessage.ProcessedAt = clock.UtcNow;
                    domainMessage.LastError = null;
                    domainMessage.LockedBy = null;
                    domainMessage.LockedUntil = null;
                }

                logger.LogDebug("Successfully published outbox message {MessageId}", message.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);

                // Find and update the domain entity for retry
                var domainMessage = await dbContext.OutboxMessages.FindAsync(new object[] { message.Id }, cancellationToken);
                if (domainMessage != null)
                {
                    domainMessage.RetryCount++;
                    domainMessage.LastError = ex.Message.Length > 4000
                        ? ex.Message.Substring(0, 4000)
                        : ex.Message;
                    domainMessage.NextRetryAt = CalculateNextRetryTime(domainMessage.RetryCount);
                    domainMessage.Status = OutboxMessageStatus.Pending;
                    domainMessage.LockedBy = null;
                    domainMessage.LockedUntil = null;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    protected virtual async Task CleanupProcessedMessagesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var cutoffDate = clock.UtcNow - options.RetentionPeriod;

        var processedMessages = await dbContext.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Processed && m.ProcessedAt != null && m.ProcessedAt < cutoffDate)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (processedMessages.Any())
        {
            logger.LogInformation("Cleaning up {Count} processed outbox messages", processedMessages.Count);
            dbContext.OutboxMessages.RemoveRange(processedMessages);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private DateTime CalculateNextRetryTime(int retryCount)
    {
        // Exponential backoff: baseDelay * 2^(retryCount - 1)
        var delay = options.RetryBaseDelay * Math.Pow(2, retryCount - 1);
        return clock.UtcNow.Add(TimeSpan.FromMilliseconds(delay.TotalMilliseconds));
    }
}