using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Manages partition lease ownership for inbox and outbox workers.
/// Workers acquire leases on logical partitions (0–63) and only process messages in owned partitions.
/// </summary>
public interface IPartitionLeaseStore
{
    /// <summary>
    /// Atomically acquires or renews up to <paramref name="maxPartitions"/> partition leases for the caller.
    /// Partitions already owned by <paramref name="ownerId"/> are renewed; free or expired partitions are
    /// newly claimed.
    /// </summary>
    /// <param name="workerName">The worker group name (e.g. "inbox", "outbox").</param>
    /// <param name="ownerId">The stable identity of the calling pod/process.</param>
    /// <param name="maxPartitions">Maximum number of partitions this pod may own simultaneously.</param>
    /// <param name="leaseDuration">How long each lease is held before it expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The set of partition numbers currently owned by this pod (may be empty if all are taken).</returns>
    Task<IReadOnlyList<int>> AcquireOrRenewAsync(
        string workerName,
        string ownerId,
        int maxPartitions,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases all partition leases held by <paramref name="ownerId"/> in the given worker group.
    /// Called on graceful shutdown so other pods can claim the partitions immediately.
    /// </summary>
    Task ReleaseAllAsync(
        string workerName,
        string ownerId,
        CancellationToken cancellationToken = default);
}
