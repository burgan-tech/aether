using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Polling;

/// <summary>
/// Default <see cref="IPollingWakeSignal{TMarker}"/> over a bounded <see cref="SemaphoreSlim"/>(0,1):
/// signals coalesce (a second Signal while one is pending is a no-op), so a burst of producers
/// causes exactly one early wake.
/// </summary>
public sealed class PollingWakeSignal<TMarker> : IPollingWakeSignal<TMarker>
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending — coalesce.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _semaphore.WaitAsync(timeout, cancellationToken);
}
