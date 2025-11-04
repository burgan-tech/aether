using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Background service that processes outbox messages.
/// Retries failed messages with exponential backoff and cleans up old processed messages.
/// </summary>
public class OutboxProcessor<TDbContext>(
    IServiceProvider serviceProvider,
    ILogger<OutboxProcessor<TDbContext>> logger,
    AetherOutboxOptions options)
    : BackgroundService
    where TDbContext : DbContext, IHasOutbox
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxProcessor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxMessagesAsync(stoppingToken);
                await CleanupProcessedMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing outbox messages");
            }

            await Task.Delay(options.ProcessingInterval, stoppingToken);
        }

        logger.LogInformation("OutboxProcessor stopped");
    }

    private async Task ProcessOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IDistributedEventBus>();
        var eventBusOptions = scope.ServiceProvider.GetRequiredService<AetherEventBusOptions>();
        var now = DateTime.UtcNow;

        // Get unprocessed messages or messages that are due for retry
        var messages = await dbContext.OutboxMessages
            .Where(m => m.ProcessedAt == null &&
                       m.RetryCount < options.MaxRetryCount &&
                       (m.NextRetryAt == null || m.NextRetryAt <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (!messages.Any())
        {
            return;
        }

        logger.LogInformation("Processing {Count} outbox messages", messages.Count);

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // Get topicName and pubSubName from ExtraProperties
                var topicName = message.ExtraProperties.GetOrDefault("TopicName")?.ToString() ?? message.EventName;
                var pubSubName = message.ExtraProperties.GetOrDefault("PubSubName")?.ToString() ?? eventBusOptions.PubSubName;
                
                logger.LogDebug("Publishing outbox message {MessageId} of type {EventName} to topic {TopicName}", 
                    message.Id, message.EventName, topicName);
                
                // EventData is already serialized CloudEventEnvelope bytes
                var serializedEnvelope = message.EventData;
                
                // Publish using IDistributedEventBus abstraction
                await eventBus.PublishEnvelopeAsync(serializedEnvelope, topicName, pubSubName, cancellationToken);

                // Mark as processed
                message.ProcessedAt = DateTime.UtcNow;
                message.LastError = null;

                logger.LogDebug("Successfully published outbox message {MessageId}", message.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);
                
                message.RetryCount++;
                message.LastError = ex.Message.Length > 4000 
                    ? ex.Message.Substring(0, 4000) 
                    : ex.Message;
                message.NextRetryAt = CalculateNextRetryTime(message.RetryCount);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupProcessedMessagesAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var cutoffDate = DateTime.UtcNow - options.RetentionPeriod;

        var processedMessages = await dbContext.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoffDate)
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
        return DateTime.UtcNow.Add(TimeSpan.FromMilliseconds(delay.TotalMilliseconds));
    }

}

