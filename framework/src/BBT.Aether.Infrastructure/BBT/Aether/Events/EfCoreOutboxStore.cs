using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Events;
using BBT.Aether.Domain.Events;
using BBT.Aether.Guids;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the outbox store.
/// </summary>
public class EfCoreOutboxStore<TDbContext>(
    TDbContext dbContext,
    IEventSerializer eventSerializer,
    IGuidGenerator guidGenerator,
    IClock clock) : IOutboxStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    public async Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // Serialize CloudEventEnvelope to bytes
        var serializedBytes = eventSerializer.Serialize(envelope);

        // Store EventName (Type) for handler resolution
        var outboxMessage = new Domain.Events.OutboxMessage(guidGenerator.Create(), envelope.Type, serializedBytes)
        {
            CreatedAt = clock.UtcNow,
            RetryCount = 0,
            ExtraProperties = {
                // Store full topic name for routing to the message broker
                ["TopicName"] = envelope.Topic ?? envelope.Type }
        };

        if (envelope.Version.HasValue)
        {
            outboxMessage.ExtraProperties["Version"] = envelope.Version.Value;
        }

        if (envelope.Source != null)
        {
            outboxMessage.ExtraProperties["Source"] = envelope.Source;
        }
        if (envelope.Subject != null)
        {
            outboxMessage.ExtraProperties["Subject"] = envelope.Subject;
        }

        await dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }
}

