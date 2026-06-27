using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// No-op implementation of <see cref="IWorkerSlotStore"/> used when partitioning is disabled.
/// Always returns slot 0, making every pod an active executor (previous behaviour).
/// </summary>
public sealed class NullWorkerSlotStore : IWorkerSlotStore
{
    public Task<int?> TryAcquireSlotAsync(string workerName, string ownerId, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(0);

    public Task<bool> RenewSlotAsync(string workerName, int slotNo, string ownerId, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task ReleaseSlotAsync(string workerName, int slotNo, string ownerId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
