using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Null implementation of the inbox lease store. Used when no database provider is configured.</summary>
public class NullInboxLeaseStore : IInboxLeaseStore
{
    public Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<int>? partitionNos = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InboxMessage>>(Array.Empty<InboxMessage>());
}
