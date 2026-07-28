using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for leasing outbox messages for processing with database-level locking.
/// </summary>
public interface IOutboxLeaseStore
{
    /// <summary>
    /// Leases a batch of outbox messages for processing with database-level locking.
    /// </summary>
    /// <remarks>
    /// Also reclaims messages left in <c>Processing</c> status whose lease has expired (e.g. a worker
    /// crashed after leasing but before writing outcomes), incrementing their <c>RetryCount</c> in the
    /// same update so repeated reclaims are observable and crash-loops can be bounded downstream.
    /// </remarks>
    /// <param name="batchSize">Maximum number of messages to lease</param>
    /// <param name="workerId">Unique identifier for the worker acquiring the lease</param>
    /// <param name="leaseDuration">How long the lease should be held</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of leased outbox messages</returns>
    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}
