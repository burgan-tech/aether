using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Null implementation of the inbox store that does nothing.
/// Used when inbox pattern is not configured.
/// </summary>
public class NullInboxStore : IInboxStore
{
    public Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<List<InboxMessage>> GetPendingEventsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<InboxMessage>());
    }

    public Task MarkAsProcessingAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }
}