using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Domain.EntityFrameworkCore;

/// <summary>
/// Dispatches domain events to the distributed event bus with optional fallback to outbox on errors.
/// </summary>
public class DomainEventDispatcher(
    IDistributedEventBus eventBus,
    AetherDomainEventOptions options,
    ILogger<DomainEventDispatcher> logger)
    : IDomainEventDispatcher
{
    public async Task DispatchEventsAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes, CancellationToken cancellationToken = default)
    {
        foreach (var envelope in eventEnvelopes)
        {
            var @event = envelope.Event;
            var metadata = envelope.Metadata;
            
            try
            {
                logger.LogDebug("Dispatching domain event: {EventType} (Name: {EventName}, Version: {Version})", 
                    metadata.EventType.Name, metadata.EventName, metadata.Version);
                
                // Use metadata-based publish - no reflection in dispatcher!
                await eventBus.PublishAsync(@event, metadata, subject: null, useOutbox: false, cancellationToken);
                
                logger.LogDebug("Successfully dispatched domain event: {EventType}", metadata.EventType.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to publish domain event: {EventType}", metadata.EventType.Name);

                if (options.WriteToOutboxOnPublishError)
                {
                    try
                    {
                        logger.LogInformation("Writing failed event to outbox: {EventType}", metadata.EventType.Name);
                        
                        // Let the event bus handle outbox storage with proper scoping
                        // This will create a new scope internally, avoiding the infinite loop
                        // The event bus will create a proper CloudEventEnvelope with all required fields
                        await eventBus.PublishAsync(@event, metadata, subject: null, useOutbox: true, cancellationToken);
                        
                        logger.LogInformation("Successfully wrote event to outbox: {EventType}", metadata.EventType.Name);
                    }
                    catch (Exception outboxEx)
                    {
                        logger.LogError(outboxEx, "Failed to write event to outbox: {EventType}", metadata.EventType.Name);
                        // Re-throw to ensure the transaction fails
                        throw;
                    }
                }
                else
                {
                    // Re-throw to ensure the transaction fails if outbox fallback is not enabled
                    throw;
                }
            }
        }
    }
}

