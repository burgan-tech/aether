using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Null implementation of the inbox store. Used when inbox is not configured.</summary>
public class NullInboxStore : IInboxStore
{
    public Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
