using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the inbox store.
/// </summary>
public class EfCoreInboxStore<TDbContext>(
    TDbContext dbContext,
    IEventSerializer eventSerializer) : IInboxStore
    where TDbContext : DbContext, IHasInbox
{
    public async Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return await dbContext.InboxMessages
            .AnyAsync(m => m.Id == eventId, cancellationToken);
    }

    public async Task MarkProcessedAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        // Serialize CloudEventEnvelope to bytes
        var serializedBytes = eventSerializer.Serialize(envelope);
        
        var inboxMessage = new InboxMessage(envelope.Id, envelope.Type, serializedBytes)
        {
            CreatedAt = now,
            Status = IncomingEventStatus.Processed
        };
        
        inboxMessage.MarkAsProcessed(now);

        // Store metadata in ExtraProperties
        if (envelope.Source != null)
        {
            inboxMessage.ExtraProperties["Source"] = envelope.Source;
        }
        if (envelope.Subject != null)
        {
            inboxMessage.ExtraProperties["Subject"] = envelope.Subject;
        }

        await dbContext.InboxMessages.AddAsync(inboxMessage, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

