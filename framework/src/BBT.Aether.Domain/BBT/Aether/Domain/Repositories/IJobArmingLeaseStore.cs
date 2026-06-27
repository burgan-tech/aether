using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
#nullable enable

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Atomically claims batches of due background jobs for arming, preventing multiple pods from
/// processing the same jobs in the same tick. Provider-specific implementations (e.g.
/// <c>NpgsqlJobArmingLeaseStore</c>) use <c>FOR UPDATE SKIP LOCKED</c> for full isolation.
/// </summary>
public interface IJobArmingLeaseStore
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> jobs that are due for arming.
    /// Sets <see cref="BackgroundJobInfo.ArmingToken"/> and <see cref="BackgroundJobInfo.ArmingUntil"/>
    /// on each claimed row within a transaction.
    /// </summary>
    /// <param name="batchSize">Maximum number of jobs to claim.</param>
    /// <param name="workerId">Stable identifier of this pod/worker (for diagnostics).</param>
    /// <param name="leaseDuration">How long the arming claim is held.</param>
    /// <param name="partitionNos">
    /// When non-null and non-empty, only jobs in these logical partitions are considered.
    /// Pass <c>null</c> to claim from all partitions (backward-compatible behaviour).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claimed jobs. Empty when nothing is due or all due rows are held by other pods.</returns>
    Task<IReadOnlyList<BackgroundJobArmingClaim>> ClaimBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<int>? partitionNos = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A single job claim returned by <see cref="IJobArmingLeaseStore.ClaimBatchAsync"/>.</summary>
/// <param name="Job">The claimed job entity.</param>
/// <param name="OriginalStatus">The status at claim time (Pending or Retrying) — used to revert on failure.</param>
/// <param name="ArmingToken">The token stamped onto the row; passed to <see cref="IJobStore.TryTransitionFromArmingAsync"/>.</param>
public record BackgroundJobArmingClaim(
    BackgroundJobInfo Job,
    BackgroundJobStatus OriginalStatus,
    Guid ArmingToken);
