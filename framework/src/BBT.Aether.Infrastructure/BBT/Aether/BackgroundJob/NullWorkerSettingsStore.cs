using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// No-op implementation of <see cref="IWorkerSettingsStore"/> used when partitioning is disabled.
/// Returns a synthetic <see cref="WorkerSettings"/> built from the provided default; writes are discarded.
/// </summary>
public sealed class NullWorkerSettingsStore : IWorkerSettingsStore
{
    public Task<WorkerSettings?> GetAsync(string workerName, CancellationToken ct = default)
        => Task.FromResult<WorkerSettings?>(null);

    public Task<WorkerSettings> GetOrDefaultAsync(
        string workerName,
        int defaultDesiredSlotCount,
        CancellationToken ct = default)
        => Task.FromResult(new WorkerSettings
        {
            WorkerName = workerName,
            DesiredSlotCount = defaultDesiredSlotCount,
            MinSlotCount = 0,
            MaxSlotCount = defaultDesiredSlotCount,
            UpdatedAt = DateTime.UtcNow
        });

    public Task UpdateDesiredSlotCountAsync(
        string workerName,
        int desiredSlotCount,
        string? updatedBy,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ReconcileSlotsAsync(
        string workerName,
        int desiredSlotCount,
        int maxSlotCount,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
