using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Partitioning;

/// <summary>
/// EF Core implementation of <see cref="IPartitionSeedService"/>. Uses <c>AddRangeAsync</c> with
/// ignore-on-conflict semantics by checking for existing rows before inserting.
/// </summary>
public class EfCorePartitionSeedService<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider)
    : IPartitionSeedService
    where TDbContext : DbContext, IHasEfCoreWorkerSlots, IHasEfCorePartitionLeases
{
    /// <inheritdoc />
    public async Task SeedWorkerSlotsAsync(
        string workerName,
        int slotCount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerName)) throw new ArgumentException("Worker name required.", nameof(workerName));
        if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var existing = await dbContext.WorkerSlots
            .Where(s => s.WorkerName == workerName)
            .Select(s => s.SlotNo)
            .ToListAsync(cancellationToken);

        var missing = Enumerable.Range(0, slotCount)
            .Where(s => !existing.Contains(s))
            .Select(s => new WorkerSlot
            {
                WorkerName = workerName,
                SlotNo = s,
                UpdatedAt = DateTime.UtcNow
            })
            .ToList();

        if (missing.Count > 0)
            await dbContext.WorkerSlots.AddRangeAsync(missing, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SeedPartitionLeasesAsync(
        string workerName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerName)) throw new ArgumentException("Worker name required.", nameof(workerName));

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var existing = await dbContext.PartitionLeases
            .Where(p => p.WorkerName == workerName)
            .Select(p => p.PartitionNo)
            .ToListAsync(cancellationToken);

        var missing = Enumerable.Range(0, LogicalPartitioner.PartitionCount)
            .Where(p => !existing.Contains(p))
            .Select(p => new PartitionLease
            {
                WorkerName = workerName,
                PartitionNo = p,
                UpdatedAt = DateTime.UtcNow
            })
            .ToList();

        if (missing.Count > 0)
            await dbContext.PartitionLeases.AddRangeAsync(missing, cancellationToken);
    }
}
