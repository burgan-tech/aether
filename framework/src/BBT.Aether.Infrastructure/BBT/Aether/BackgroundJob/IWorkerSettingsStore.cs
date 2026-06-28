using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Reads and writes the runtime slot configuration stored in the <c>worker_settings</c> table.
/// </summary>
public interface IWorkerSettingsStore
{
    /// <summary>
    /// Returns the settings for the given worker, or <c>null</c> if the row does not exist.
    /// </summary>
    Task<WorkerSettings?> GetAsync(string workerName, CancellationToken ct = default);

    /// <summary>
    /// Returns the settings for the given worker. If the row does not exist a transient instance
    /// with <see cref="WorkerSettings.DesiredSlotCount"/> set to <paramref name="defaultDesiredSlotCount"/>
    /// is returned (nothing is persisted).
    /// </summary>
    Task<WorkerSettings> GetOrDefaultAsync(
        string workerName,
        int defaultDesiredSlotCount,
        CancellationToken ct = default);

    /// <summary>
    /// Updates <see cref="WorkerSettings.DesiredSlotCount"/> for the named worker.
    /// The caller is responsible for enforcing min/max bounds before calling this method.
    /// </summary>
    Task UpdateDesiredSlotCountAsync(
        string workerName,
        int desiredSlotCount,
        string? updatedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Reconciles the <c>worker_slots</c> table so that slots 0 … <paramref name="desiredSlotCount"/>-1
    /// are enabled and any slots with a higher index are disabled.
    /// </summary>
    Task ReconcileSlotsAsync(
        string workerName,
        int desiredSlotCount,
        int maxSlotCount,
        CancellationToken ct = default);
}
