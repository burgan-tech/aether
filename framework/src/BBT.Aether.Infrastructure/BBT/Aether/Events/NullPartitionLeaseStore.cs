using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Partitioning;

namespace BBT.Aether.Events;

/// <summary>
/// No-op implementation of <see cref="IPartitionLeaseStore"/> used when partitioning is disabled.
/// Claims all 64 partitions so every pod processes every message (previous behaviour).
/// </summary>
public sealed class NullPartitionLeaseStore : IPartitionLeaseStore
{
    private static readonly IReadOnlyList<int> AllPartitions =
        Enumerable.Range(0, LogicalPartitioner.PartitionCount).ToArray();

    public Task<IReadOnlyList<int>> AcquireOrRenewAsync(string workerName, string ownerId, int maxPartitions,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromResult(AllPartitions);

    public Task ReleaseAllAsync(string workerName, string ownerId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
