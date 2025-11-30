using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Null implementation of the outbox store that does nothing.
/// Used when outbox pattern is not configured.
/// </summary>
public class NullOutboxStore : IOutboxStore
{
    public Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
    }
}