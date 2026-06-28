using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// EF Core implementation of <see cref="IWorkerSettingsStore"/>.
/// Used as the fallback when no provider-specific override is registered.
/// Reconciliation uses EF Core LINQ to update slots in two passes (enable / disable).
/// </summary>
public class EfCoreWorkerSettingsStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider)
    : IWorkerSettingsStore
    where TDbContext : DbContext, IHasEfCoreWorkerSettings, IHasEfCoreWorkerSlots
{
    /// <inheritdoc />
    public async Task<WorkerSettings?> GetAsync(string workerName, CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        return await dbContext.WorkerSettings.FindAsync([workerName], ct);
    }

    /// <inheritdoc />
    public async Task<WorkerSettings> GetOrDefaultAsync(
        string workerName,
        int defaultDesiredSlotCount,
        CancellationToken ct = default)
    {
        var settings = await GetAsync(workerName, ct);
        return settings ?? new WorkerSettings
        {
            WorkerName = workerName,
            DesiredSlotCount = defaultDesiredSlotCount,
            MinSlotCount = 0,
            MaxSlotCount = defaultDesiredSlotCount,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task UpdateDesiredSlotCountAsync(
        string workerName,
        int desiredSlotCount,
        string? updatedBy,
        CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var settings = await dbContext.WorkerSettings.FindAsync([workerName], ct);
        if (settings == null) return;

        settings.DesiredSlotCount = desiredSlotCount;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = updatedBy;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task ReconcileSlotsAsync(
        string workerName,
        int desiredSlotCount,
        int maxSlotCount,
        CancellationToken ct = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(ct);
        var now = DateTime.UtcNow;

        // Enable slots 0..desired-1 (insert missing, enable existing disabled rows)
        var existing = await dbContext.WorkerSlots
            .Where(s => s.WorkerName == workerName)
            .ToDictionaryAsync(s => s.SlotNo, ct);

        for (var i = 0; i < desiredSlotCount; i++)
        {
            if (existing.TryGetValue(i, out var slot))
            {
                if (!slot.IsEnabled)
                {
                    slot.IsEnabled = true;
                    slot.UpdatedAt = now;
                }
            }
            else
            {
                await dbContext.WorkerSlots.AddAsync(new WorkerSlot
                {
                    WorkerName = workerName,
                    SlotNo = i,
                    IsEnabled = true,
                    UpdatedAt = now
                }, ct);
            }
        }

        // Disable slots >= desired
        foreach (var slot in existing.Values.Where(s => s.SlotNo >= desiredSlotCount && s.IsEnabled))
        {
            slot.IsEnabled = false;
            slot.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(ct);
    }
}
