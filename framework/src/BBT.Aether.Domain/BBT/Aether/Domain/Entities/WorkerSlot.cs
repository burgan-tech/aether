using System;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Represents a worker slot that limits the number of active background job executors.
/// Only a pod that holds a slot may process background jobs; pods without a slot stand by passively.
/// The table is pre-seeded with a fixed number of rows (one per allowed active executor).
/// </summary>
public class WorkerSlot
{
    /// <summary>The logical name of the worker group (e.g. "background-job").</summary>
    public string WorkerName { get; set; } = default!;

    /// <summary>The 0-based slot index.</summary>
    public int SlotNo { get; set; }

    /// <summary>The stable owner identifier of the pod currently holding this slot. Null when free.</summary>
    public string? OwnerId { get; set; }

    /// <summary>UTC time after which the slot is considered expired and may be claimed by another pod.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Whether this slot is available for acquisition. Set to <c>false</c> when
    /// <c>desired_slot_count</c> is reduced at runtime; the row is never deleted so that
    /// an active owner can finish its current lease gracefully.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>UTC time of the last update (for observability).</summary>
    public DateTime UpdatedAt { get; set; }
}
