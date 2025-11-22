using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the inbox store.
/// </summary>
public class EfCoreInboxStore<TDbContext>(
    TDbContext dbContext,
    IEventSerializer eventSerializer,
    IClock clock) : IInboxStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    public async Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return await dbContext.InboxMessages
            .AnyAsync(m => m.Id == eventId && m.Status == IncomingEventStatus.Processed, cancellationToken);
    }

    public async Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsProcessed(clock.UtcNow);
        }
    }

    public async Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        // Serialize CloudEventEnvelope to bytes
        var serializedBytes = eventSerializer.Serialize(envelope);

        // Store EventName (Type) for handler resolution
        var inboxMessage = new Domain.Events.InboxMessage(envelope.Id, envelope.Type, serializedBytes)
        {
            CreatedAt = now,
            Status = IncomingEventStatus.Pending
        };

        // Store metadata in ExtraProperties
        if (envelope.Topic != null)
        {
            inboxMessage.ExtraProperties["Topic"] = envelope.Topic;
        }

        if (envelope.Version.HasValue)
        {
            inboxMessage.ExtraProperties["Version"] = envelope.Version.Value;
        }

        if (envelope.Source != null)
        {
            inboxMessage.ExtraProperties["Source"] = envelope.Source;
        }

        if (envelope.Subject != null)
        {
            inboxMessage.ExtraProperties["Subject"] = envelope.Subject;
        }

        await dbContext.InboxMessages.AddAsync(inboxMessage, cancellationToken);
        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    public async Task<List<InboxMessage>> GetPendingEventsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var domainMessages = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        // Convert Domain.Events.InboxMessage to Abstractions.InboxMessage
        return domainMessages.Select(dm => new InboxMessage
        {
            Id = dm.Id,
            EventName = dm.EventName,
            EventData = dm.EventData,
            CreatedAt = dm.CreatedAt,
            Status = dm.Status,
            HandledTime = dm.HandledTime,
            RetryCount = dm.RetryCount,
            NextRetryTime = dm.NextRetryTime,
            ExtraProperties = dm.ExtraProperties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value!)
        }).ToList();
    }

    public async Task MarkAsProcessingAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsProcessing();
        }
    }

    public async Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsDiscarded(clock.UtcNow);
        }
    }

    public async Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = clock.UtcNow - retentionPeriod;

        var oldMessages = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Processed &&
                        m.HandledTime != null &&
                        m.HandledTime < cutoffDate)
            .OrderBy(m => m.HandledTime)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (!oldMessages.Any())
            return 0;

        dbContext.InboxMessages.RemoveRange(oldMessages);
        return await dbContext.SaveChangesAsync(cancellationToken);
    }
}