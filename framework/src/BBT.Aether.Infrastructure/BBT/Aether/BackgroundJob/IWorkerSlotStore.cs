using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Manages worker slot leases that control how many pods may act as active background job executors.
/// The slot table is pre-seeded with a fixed number of rows; only a pod that acquires a slot processes jobs.
/// </summary>
public interface IWorkerSlotStore
{
    /// <summary>
    /// Attempts to acquire any available slot for <paramref name="ownerId"/> in the given worker group.
    /// If <paramref name="ownerId"/> already holds a slot it is renewed in place.
    /// </summary>
    /// <param name="workerName">The worker group name (e.g. "background-job").</param>
    /// <param name="ownerId">The stable identity of the calling pod/process.</param>
    /// <param name="leaseDuration">How long the slot is held before it expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The acquired slot number, or <c>null</c> if all slots are taken by other owners.</returns>
    Task<int?> TryAcquireSlotAsync(
        string workerName,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the lease on a slot already held by <paramref name="ownerId"/>.
    /// </summary>
    /// <returns><c>true</c> if the slot was still owned by this pod and the lease was extended;
    /// <c>false</c> if ownership was lost (lease expired and taken by another pod).</returns>
    Task<bool> RenewSlotAsync(
        string workerName,
        int slotNo,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the slot so another pod may acquire it immediately.
    /// </summary>
    Task ReleaseSlotAsync(
        string workerName,
        int slotNo,
        string ownerId,
        CancellationToken cancellationToken = default);
}
