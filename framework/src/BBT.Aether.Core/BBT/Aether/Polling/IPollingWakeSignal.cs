using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Polling;

/// <summary>
/// A coalescing wake signal for adaptive polling loops. Producers call <see cref="Signal"/> when
/// new work becomes available; the polling loop awaits <see cref="WaitAsync"/> with its normal
/// interval as the timeout so a signal cuts the wait short while polling remains the safety net.
/// The marker type parameter distinguishes independent loops (e.g. outbox vs inbox) in DI.
/// </summary>
/// <typeparam name="TMarker">Marker type identifying the loop this signal wakes.</typeparam>
public interface IPollingWakeSignal<TMarker>
{
    /// <summary>Wakes the loop. Multiple pending signals coalesce into one.</summary>
    void Signal();

    /// <summary>
    /// Waits until <see cref="Signal"/> is called or the timeout elapses.
    /// Returns true when woken by a signal, false on timeout.
    /// </summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
