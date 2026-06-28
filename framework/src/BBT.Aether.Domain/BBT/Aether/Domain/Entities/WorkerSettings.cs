using System;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Stores the runtime slot configuration for a named worker group.
/// The <see cref="DesiredSlotCount"/> field is the live control knob updated via the Admin API;
/// the poller reads it on every reconciliation cycle so changes take effect without a restart.
/// </summary>
public class WorkerSettings
{
    /// <summary>The logical name of the worker group (e.g. "background-job").</summary>
    public string WorkerName { get; set; } = default!;

    /// <summary>Current target number of active executor slots (0 … <see cref="MaxSlotCount"/>).</summary>
    public int DesiredSlotCount { get; set; }

    /// <summary>Hard lower bound enforced by the Admin API.</summary>
    public int MinSlotCount { get; set; }

    /// <summary>Hard upper bound enforced by the Admin API.</summary>
    public int MaxSlotCount { get; set; }

    /// <summary>UTC time of the last settings update.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Identity of the operator who last changed the settings (free-form, nullable).</summary>
    public string? UpdatedBy { get; set; }
}
