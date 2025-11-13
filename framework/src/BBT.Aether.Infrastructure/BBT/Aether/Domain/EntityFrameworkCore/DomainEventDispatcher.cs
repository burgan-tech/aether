using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Domain.EntityFrameworkCore;

/// <summary>
/// Dispatches domain events to the distributed event bus with optional fallback to outbox on errors.
/// </summary>
public class DomainEventDispatcher(
    IDistributedEventBus eventBus,
    AetherDomainEventOptions options,
    ILogger<DomainEventDispatcher> logger,
    IServiceProvider serviceProvider)
    : IDomainEventDispatcher
{
    public async Task DispatchEventsAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes,
        CancellationToken cancellationToken = default)
    {
        // For AlwaysUseOutbox strategy, write directly to outbox within transaction
        // For backwards compatibility, also support the old WriteToOutboxOnPublishError flag
        var useOutboxDirectly = options.DispatchStrategy == DomainEventDispatchStrategy.AlwaysUseOutbox;
        
        foreach (var envelope in eventEnvelopes)
        {
            var @event = envelope.Event;
            var metadata = envelope.Metadata;

            if (useOutboxDirectly)
            {
                logger.LogDebug("Writing domain event to outbox: {EventType} (Name: {EventName}, Version: {Version})",
                    metadata.EventType.Name, metadata.EventName, metadata.Version);

                // Write directly to outbox within the transaction
                await eventBus.PublishAsync(@event, metadata, 
                    subject: EventSubjectExtractor.ExtractSubject(@event),
                    useOutbox: true, cancellationToken);

                logger.LogDebug("Successfully wrote domain event to outbox: {EventType}", metadata.EventType.Name);
            }
            else
            {
                try
                {
                    logger.LogDebug("Dispatching domain event: {EventType} (Name: {EventName}, Version: {Version})",
                        metadata.EventType.Name, metadata.EventName, metadata.Version);

                    await eventBus.PublishAsync(@event, metadata, subject: EventSubjectExtractor.ExtractSubject(@event),
                        useOutbox: false, cancellationToken);

                    logger.LogDebug("Successfully dispatched domain event: {EventType}", metadata.EventType.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to publish domain event: {EventType}", metadata.EventType.Name);
                }
            }
        }
    }

    public async Task PublishDirectlyAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes,
        CancellationToken cancellationToken = default)
    {
        foreach (var envelope in eventEnvelopes)
        {
            var @event = envelope.Event;
            var metadata = envelope.Metadata;

            logger.LogDebug("Publishing domain event directly: {EventType} (Name: {EventName}, Version: {Version})",
                metadata.EventType.Name, metadata.EventName, metadata.Version);

            // Publish directly without try-catch - let caller handle failures
            await eventBus.PublishAsync(@event, metadata, subject: EventSubjectExtractor.ExtractSubject(@event),
                useOutbox: false, cancellationToken);

            logger.LogDebug("Successfully published domain event directly: {EventType}", metadata.EventType.Name);
        }
    }

    public async Task WriteToOutboxInNewScopeAsync(IEnumerable<DomainEventEnvelope> eventEnvelopes,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Direct publish failed, writing events to outbox in new scope as fallback");

        // Create new scope to avoid ambient UoW
        await using var scope = serviceProvider.CreateAsyncScope();
        var scopedEventBus = scope.ServiceProvider.GetRequiredService<IDistributedEventBus>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        // Begin a new unit of work with Suppress to avoid ambient
        await using var uow = await uowManager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
            cancellationToken);

        foreach (var envelope in eventEnvelopes)
        {
            var @event = envelope.Event;
            var metadata = envelope.Metadata;

            logger.LogInformation("Writing event to outbox: {EventType} (Name: {EventName}, Version: {Version})",
                metadata.EventType.Name, metadata.EventName, metadata.Version);

            // Write to outbox using the scoped event bus
            await scopedEventBus.PublishAsync(@event, metadata,
                subject: EventSubjectExtractor.ExtractSubject(@event),
                useOutbox: true, cancellationToken);
        }

        // Commit the new transaction to persist events to outbox
        await uow.SaveChangesAsync(cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.LogInformation("Successfully wrote {Count} events to outbox in new scope", 
            eventEnvelopes.Count());
    }
}