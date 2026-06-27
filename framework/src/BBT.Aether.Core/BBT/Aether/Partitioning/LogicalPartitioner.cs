using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;

namespace BBT.Aether.Partitioning;

/// <summary>
/// Computes logical partition numbers and slot-to-partition assignments for distributed queue processing.
/// Partition numbers are stable: the same key always maps to the same partition regardless of cluster size.
/// </summary>
public static class LogicalPartitioner
{
    /// <summary>Total number of logical partitions.</summary>
    public const int PartitionCount = 64;

    /// <summary>
    /// Returns the logical partition number for the given key in the range [0, <see cref="PartitionCount"/>).
    /// Uses XxHash32 for a fast, uniform distribution.
    /// </summary>
    public static int GetPartitionNo(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Partition key cannot be empty.", nameof(key));

        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = XxHash32.HashToUInt32(bytes);
        return (int)(hash % PartitionCount);
    }

    /// <summary>
    /// Returns the set of partition numbers owned by the given slot using modulo distribution.
    /// Example: slot 0 of 2 owns partitions 0, 2, 4, … 62; slot 1 owns 1, 3, 5, … 63.
    /// Modulo (rather than range) is used so the distribution remains balanced even when
    /// <paramref name="slotCount"/> does not evenly divide <see cref="PartitionCount"/>.
    /// </summary>
    /// <param name="slotNo">The 0-based slot index.</param>
    /// <param name="slotCount">Total number of active slots.</param>
    public static IReadOnlyList<int> GetPartitionsForSlot(int slotNo, int slotCount)
    {
        if (slotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Slot count must be positive.");

        if (slotNo < 0 || slotNo >= slotCount)
            throw new ArgumentOutOfRangeException(nameof(slotNo), $"Slot number must be in [0, {slotCount}).");

        return Enumerable.Range(0, PartitionCount)
            .Where(p => p % slotCount == slotNo)
            .ToArray();
    }
}
