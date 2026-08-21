using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// A deferred arm for an already-persisted background job: everything the external scheduler needs is
/// captured in memory, so arming later costs exactly one scheduler call — no re-read of the job row and
/// no extra status write.
/// <para>
/// Exists for callers that must persist the job inside a critical section but cannot afford to make the
/// scheduler round-trip there. Holding a distributed lock across an external call makes that call the
/// lock's hold time, serializing every other contender behind it.
/// </para>
/// <para>
/// The row is already <c>Scheduled</c> when the handle is issued — optimistically, because the common
/// case succeeds. <see cref="ArmAsync"/> reconciles a failure by rolling the row back to
/// <c>Pending</c> so the arming poller reclaims it, which is the same contract the inline arm has.
/// </para>
/// </summary>
public interface IBackgroundJobArmHandle
{
    /// <summary>The id of the persisted job this handle arms.</summary>
    Guid JobId { get; }

    /// <summary>
    /// Arms the job in the external scheduler. Never throws: a failure is logged and the row is rolled
    /// back to <c>Pending</c> for the arming poller. Safe to call once; calling it again re-schedules
    /// the same job name, which the scheduler treats as an overwrite.
    /// </summary>
    Task ArmAsync(CancellationToken cancellationToken = default);
}
