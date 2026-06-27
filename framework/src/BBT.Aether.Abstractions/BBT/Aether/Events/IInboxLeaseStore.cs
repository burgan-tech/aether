using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for leasing inbox messages for processing with database-level locking.
/// </summary>
public interface IInboxLeaseStore
{
    /// <summary>
    /// Leases a batch of inbox messages for processing with database-level locking.
    /// </summary>
    /// <param name="batchSize">Maximum number of messages to lease</param>
    /// <param name="workerId">Unique identifier for the worker acquiring the lease</param>
    /// <param name="leaseDuration">How long the lease should be held</param>
    /// <param name="partitionNos">
    /// When non-null and non-empty, only messages in these logical partitions are considered.
    /// Pass <c>null</c> to lease from all partitions (backward-compatible behaviour).
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of leased inbox messages</returns>
    Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<int>? partitionNos = null,
        CancellationToken cancellationToken = default);
}
