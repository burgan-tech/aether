using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Partitioning;

/// <summary>
/// Seeds the <c>worker_slots</c> and <c>partition_leases</c> tables with the required number of rows.
/// Call this once at application startup (e.g. in the startup hook or a migration step).
/// Rows that already exist are left untouched (<c>ON CONFLICT DO NOTHING</c> semantics).
/// </summary>
public interface IPartitionSeedService
{
    /// <summary>
    /// Ensures the <c>worker_slots</c> table contains <paramref name="slotCount"/> rows
    /// for the given worker name (slots 0 … slotCount-1).
    /// </summary>
    Task SeedWorkerSlotsAsync(string workerName, int slotCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the <c>partition_leases</c> table contains 64 rows for the given worker name
    /// (partitions 0 … 63).
    /// </summary>
    Task SeedPartitionLeasesAsync(string workerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the <c>worker_settings</c> row exists for the given worker name.
    /// If the row already exists it is left untouched (<c>ON CONFLICT DO NOTHING</c> semantics).
    /// </summary>
    Task SeedWorkerSettingsAsync(
        string workerName,
        int initialSlotCount,
        int minSlotCount,
        int maxSlotCount,
        CancellationToken cancellationToken = default);
}
