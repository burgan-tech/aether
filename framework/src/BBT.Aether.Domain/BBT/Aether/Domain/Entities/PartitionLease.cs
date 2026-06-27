using System;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Represents ownership of a single logical partition for inbox or outbox processing.
/// Workers compete to acquire leases; a worker only processes messages in partitions it currently owns.
/// The table is pre-seeded with 64 rows per worker name (one per logical partition).
/// </summary>
public class PartitionLease
{
    /// <summary>The logical name of the worker group (e.g. "inbox", "outbox").</summary>
    public string WorkerName { get; set; } = default!;

    /// <summary>The partition number (0–63).</summary>
    public int PartitionNo { get; set; }

    /// <summary>The stable owner identifier of the pod currently holding this partition. Null when free.</summary>
    public string? OwnerId { get; set; }

    /// <summary>UTC time after which the lease is considered expired and may be claimed by another pod.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>UTC time of the last update (for observability).</summary>
    public DateTime UpdatedAt { get; set; }
}
