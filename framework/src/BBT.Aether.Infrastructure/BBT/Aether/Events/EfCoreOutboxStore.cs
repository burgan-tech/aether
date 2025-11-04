using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events;
using BBT.Aether.Guids;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the outbox store.
/// </summary>
public class EfCoreOutboxStore<TDbContext>(
    TDbContext dbContext,
    IEventSerializer eventSerializer,
    IGuidGenerator guidGenerator) : IOutboxStore
    where TDbContext : DbContext, IHasOutbox
{
    public async Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // Serialize CloudEventEnvelope to bytes
        var serializedBytes = eventSerializer.Serialize(envelope);

        var outboxMessage = new OutboxMessage(guidGenerator.Create(), envelope.Type, serializedBytes)
        {
            CreatedAt = DateTime.UtcNow,
            RetryCount = 0,
            ExtraProperties = {
                // Store metadata in ExtraProperties
                ["TopicName"] = envelope.Type }
        };

        if (envelope.Source != null)
        {
            outboxMessage.ExtraProperties["Source"] = envelope.Source;
        }
        if (envelope.Subject != null)
        {
            outboxMessage.ExtraProperties["Subject"] = envelope.Subject;
        }

        await dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

