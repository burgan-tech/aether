using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Null implementation of the outbox lease store. Used when no database provider is configured.</summary>
public class NullOutboxLeaseStore : IOutboxLeaseStore
{
    public Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
}
