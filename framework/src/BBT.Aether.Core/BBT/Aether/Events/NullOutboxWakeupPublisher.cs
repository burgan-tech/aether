using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Default publisher used when no broker-backed implementation is registered.
/// Signals are simply dropped; the dispatcher's fallback polling still finds the rows.
/// </summary>
public sealed class NullOutboxWakeupPublisher : IOutboxWakeupPublisher
{
    public Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
