using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Opt-in capability for job stores that provide the status-guarded arming and terminal-cleanup
/// operations required by <c>BackgroundJobArmingProcessor</c>. Legacy <see cref="IJobStore"/>
/// implementations remain source-compatible but are not armed until they explicitly implement this contract.
/// </summary>
public interface IJobArmingStore
{
    /// <summary>
    /// Finalizes or aborts an arming claim only when both its token and original waiting status still match.
    /// </summary>
    Task<bool> TryTransitionFromArmingAsync(
        Guid id,
        Guid armingToken,
        BackgroundJobStatus expectedOriginalStatus,
        BackgroundJobStatus to,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically acquires terminal scheduler-cleanup ownership.</summary>
    Task<bool> TryAcquireTerminalArmingCompensationAsync(
        Guid id,
        Guid lostArmingToken,
        Guid compensationToken,
        DateTime now,
        DateTime compensationUntil,
        CancellationToken cancellationToken = default);

    /// <summary>Renews terminal scheduler-cleanup ownership under the exact compensation token.</summary>
    Task<bool> TryRenewArmingCompensationAsync(
        Guid id,
        Guid compensationToken,
        DateTime compensationUntil,
        CancellationToken cancellationToken = default);

    /// <summary>Releases terminal scheduler-cleanup ownership under the exact compensation token.</summary>
    Task<bool> TryReleaseArmingCompensationAsync(
        Guid id,
        Guid compensationToken,
        CancellationToken cancellationToken = default);
}
