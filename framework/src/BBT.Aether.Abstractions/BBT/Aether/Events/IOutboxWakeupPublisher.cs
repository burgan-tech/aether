using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes outbox wake-up signals. Implementations must be best-effort.
/// </summary>
public interface IOutboxWakeupPublisher
{
    /// <summary>
    /// Attempts to publish a wake-up signal. Returns false on failure instead of throwing —
    /// the business transaction has already committed by this point and must not be failed by
    /// a broker problem. Only <see cref="System.OperationCanceledException"/> for a cancelled
    /// caller token may propagate.
    /// </summary>
    Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default);
}
