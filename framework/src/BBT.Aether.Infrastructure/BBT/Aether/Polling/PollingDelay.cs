using System;

namespace BBT.Aether.Polling;

/// <summary>
/// Adaptive poll pacing shared by the outbox, inbox and background-job arming loops. Lives here rather than inline in each
/// loop so the two stay identical and so the pacing rules are unit-testable without a running host.
/// </summary>
/// <remarks>
/// Every returned delay is jittered. Without it, replicas started together — which is exactly what a
/// rolling deployment produces — poll in lockstep: the fleet loses the natural staggering that makes
/// N replicas pick work up ~N times sooner than one, and each tick becomes a burst of simultaneous
/// claim queries against the same rows. Jitter is what keeps the phases spread.
/// </remarks>
internal static class PollingDelay
{
    /// <summary>Jitter applied to every delay, as a fraction either side of the nominal value.</summary>
    internal const double JitterFraction = 0.25;

    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The delay after a round that found work: poll again almost immediately, since a queue that had
    /// one item usually has more.
    /// </summary>
    internal static TimeSpan OnProcessed(TimeSpan busyInterval) => busyInterval;

    /// <summary>
    /// The delay after an empty round: double it, capped, so a quiet system stops paying for polls.
    /// </summary>
    internal static TimeSpan OnEmpty(TimeSpan current, TimeSpan max) => MinOf(Double(current), max);

    /// <summary>
    /// The delay after a failed round. Backs off one step like an empty round, but never below
    /// <paramref name="idleInterval"/> so a hard failure is not retried at the busy cadence.
    /// <para>
    /// Deliberately NOT a jump straight to <paramref name="max"/>. That is what the loops used to do,
    /// and with several replicas a single transient fault — a brief database hiccup, one poison
    /// message — stalled the entire fleet for a full maximum interval, right when it was most needed.
    /// Escalating instead keeps a one-off blip cheap while a persistent fault still ends up at the cap.
    /// </para>
    /// </summary>
    internal static TimeSpan OnError(TimeSpan current, TimeSpan idleInterval, TimeSpan max)
        => MinOf(MaxOf(Double(current), idleInterval), max);

    /// <summary>
    /// Applies <see cref="JitterFraction"/> to a delay using a caller-supplied uniform sample in
    /// [0, 1), so tests can pin the arithmetic.
    /// </summary>
    internal static TimeSpan Jitter(TimeSpan delay, double sample)
    {
        var scale = 1.0 - JitterFraction + (2.0 * JitterFraction * sample);
        var jittered = TimeSpan.FromTicks((long)(delay.Ticks * scale));
        return jittered < Floor ? Floor : jittered;
    }

    /// <summary>Applies jitter using the shared random source.</summary>
    internal static TimeSpan Jitter(TimeSpan delay) => Jitter(delay, Random.Shared.NextDouble());

    /// <summary>
    /// A random delay in [0, <paramref name="idleInterval"/>) to spread the first poll of replicas that
    /// started at the same moment. Bounded by the idle interval, not the maximum, so a fresh
    /// deployment never sits idle for a whole cap before its first round.
    /// </summary>
    internal static TimeSpan StartupOffset(TimeSpan idleInterval, double sample)
        => TimeSpan.FromTicks((long)(idleInterval.Ticks * sample));

    /// <inheritdoc cref="StartupOffset(TimeSpan, double)"/>
    internal static TimeSpan StartupOffset(TimeSpan idleInterval)
        => StartupOffset(idleInterval, Random.Shared.NextDouble());

    private static TimeSpan Double(TimeSpan value) => TimeSpan.FromTicks(value.Ticks * 2);

    private static TimeSpan MinOf(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan MaxOf(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
