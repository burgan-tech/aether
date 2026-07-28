using System;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Adaptive polling delay policy shared by the inbox and outbox dispatchers.
/// </summary>
/// <remarks>
/// A full batch means more work is almost certainly waiting, so poll again immediately.
/// A partial batch means the queue just drained — returning to the busy interval would
/// force roughly ten wasted polls climbing back to the cap, which dominated the
/// dispatcher's database cost in production measurements.
/// </remarks>
public static class AdaptivePolling
{
    /// <summary>
    /// Computes the delay before the next dispatcher poll.
    /// </summary>
    /// <param name="current">The delay used before the poll that just completed.</param>
    /// <param name="processed">Number of messages handled by the poll that just completed.</param>
    /// <param name="batchSize">The configured lease batch size.</param>
    /// <param name="busyInterval">Delay to use when a full batch was returned.</param>
    /// <param name="idleInterval">Delay to use when a partial batch was returned.</param>
    /// <param name="maxInterval">Upper bound for the exponential idle backoff.</param>
    public static TimeSpan NextDelay(
        TimeSpan current,
        int processed,
        int batchSize,
        TimeSpan busyInterval,
        TimeSpan idleInterval,
        TimeSpan maxInterval)
    {
        if (processed >= batchSize && batchSize > 0) return busyInterval;
        if (processed > 0) return idleInterval;

        var next = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
        return next > maxInterval ? maxInterval : next;
    }
}
