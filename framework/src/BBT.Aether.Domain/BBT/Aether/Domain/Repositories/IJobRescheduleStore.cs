using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Optional job-store capability for atomically rescheduling an existing waiting job.
/// Implementations must never move Running or terminal rows back to Pending.
/// </summary>
public interface IJobRescheduleStore
{
    /// <summary>
    /// Atomically updates the schedule only when the current status is Pending, Scheduled, or Retrying.
    /// A failed conditional update returns the current status, or null when the row does not exist.
    /// </summary>
    Task<BackgroundJobRescheduleResult> TryRescheduleWaitingAsync(
        Guid id,
        string newSchedule,
        JobKind kind,
        DateTime nextRetryAtUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an atomic waiting-job reschedule attempt.</summary>
/// <param name="Succeeded">True only when the conditional update changed the row.</param>
/// <param name="CurrentStatus">Status observed after a failed attempt, or null when the row does not exist.</param>
public readonly record struct BackgroundJobRescheduleResult(
    bool Succeeded,
    BackgroundJobStatus? CurrentStatus);
