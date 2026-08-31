using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes the <see cref="OutboxWakeupEvent"/> nudge. Implementations must be fire-and-forget
/// safe: a failed notify is swallowed by callers because polling backstops delivery.
/// </summary>
public interface IOutboxWakeupNotifier
{
    Task NotifyAsync(CancellationToken cancellationToken = default);
}
