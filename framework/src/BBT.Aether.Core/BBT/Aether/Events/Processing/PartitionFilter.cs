using System.Collections.Generic;
using System.Linq;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Turns the signal keys a dispatcher woke on into a lease-query partition filter.
/// </summary>
public static class PartitionFilter
{
    /// <summary>
    /// Returns the distinct partitions to lease from, or null meaning "lease unfiltered".
    /// </summary>
    /// <remarks>
    /// Unfiltered is the safe answer and is returned whenever the signals do not narrow things
    /// down: no signals at all means the fallback timeout fired, and a check-all signal means a
    /// producer touched more partitions in one transaction than it was worth naming
    /// individually. Fallback polling being unfiltered is what stops a partition whose signal
    /// was lost from being stranded.
    /// </remarks>
    public static IReadOnlyCollection<short>? Resolve(IReadOnlyCollection<OutboxSignalKey> keys)
    {
        if (keys.Count == 0) return null;
        if (keys.Any(k => k.PartitionId == OutboxWakeupSignal.AllPartitions)) return null;

        return keys.Select(k => k.PartitionId).Distinct().ToArray();
    }
}
