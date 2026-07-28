using System;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Decides whether a retention cleanup pass is due, so cleanup runs on an interval
/// rather than on every dispatcher poll.
/// </summary>
/// <remarks>
/// Shared by the inbox and outbox dispatchers. Running cleanup on every poll opened a
/// second transaction per cycle even when the poll leased nothing, doubling the baseline
/// database cost of an idle dispatcher.
/// </remarks>
public static class CleanupSchedule
{
    /// <summary>
    /// Returns true when at least <paramref name="interval"/> has elapsed since
    /// <paramref name="lastRunUtc"/>. Always true on the first run
    /// (<see cref="DateTime.MinValue"/>).
    /// </summary>
    public static bool IsDue(DateTime lastRunUtc, DateTime nowUtc, TimeSpan interval)
        => nowUtc - lastRunUtc >= interval;
}
