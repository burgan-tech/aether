using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Partitioning;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Entity Framework Core implementation of the inbox store.
/// </summary>
public class EfCoreInboxStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IEventSerializer eventSerializer,
    IClock clock,
    AetherInboxOptions options) : IInboxStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    public async Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.InboxMessages
            .AnyAsync(m => m.Id == eventId && m.Status == IncomingEventStatus.Processed, cancellationToken);
    }

    public async Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message != null)
        {
            message.MarkAsProcessed(clock.UtcNow);
        }
    }

    public async Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var now = clock.UtcNow;

        var serializedBytes = eventSerializer.Serialize(envelope);

        var inboxMessage = new Domain.Events.InboxMessage(envelope.Id, envelope.Type, serializedBytes)
        {
            CreatedAt = now,
            Status = IncomingEventStatus.Pending,
            PartitionNo = LogicalPartitioner.GetPartitionNo(envelope.Id)
        };

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
    }

    public async Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var message = await dbContext.InboxMessages
            .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

        if (message == null) return;

        if (message.RetryCount + 1 >= options.MaxRetryCount)
        {
            message.Status = IncomingEventStatus.DeadLetter;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
        else
        {
            message.RetryCount++;
            var delay = options.RetryBaseDelay * Math.Pow(2, message.RetryCount - 1);
            message.NextRetryTime = clock.UtcNow.Add(TimeSpan.FromMilliseconds(delay.TotalMilliseconds));
            message.Status = IncomingEventStatus.Pending;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    public async Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var cutoffDate = clock.UtcNow - retentionPeriod;

        var oldMessages = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Processed &&
                        m.HandledTime != null &&
                        m.HandledTime < cutoffDate)
            .OrderBy(m => m.HandledTime)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (oldMessages.Count == 0)
            return 0;

        var count = oldMessages.Count;
        dbContext.InboxMessages.RemoveRange(oldMessages);
        return count;
    }
}
