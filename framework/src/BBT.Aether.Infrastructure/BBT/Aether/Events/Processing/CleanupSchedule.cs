using System;
using System.Threading;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Per-processor gate that spaces out retention cleanups. Uses the monotonic tick count so a
/// wall-clock adjustment can neither stall cleanup nor make it run back to back. Due immediately
/// after construction, so a freshly started worker cleans up on its first cycle.
/// </summary>
internal sealed class CleanupSchedule
{
    private long _nextDueTickMs;

    public bool IsDue => Environment.TickCount64 >= Volatile.Read(ref _nextDueTickMs);

    public void Defer(TimeSpan interval)
        => Volatile.Write(ref _nextDueTickMs, Environment.TickCount64 + (long)interval.TotalMilliseconds);
}
