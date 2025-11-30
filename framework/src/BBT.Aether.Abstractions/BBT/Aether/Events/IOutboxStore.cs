using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for the outbox store, used for transactional event publishing.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Stores an event envelope in the outbox for later processing.
    /// </summary>
    /// <param name="envelope">The CloudEventEnvelope to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leases a batch of outbox messages for processing with database-level locking.
    /// </summary>
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

