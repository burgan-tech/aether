using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for the outbox store, used for transactional event publishing.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Stores an event envelope in the outbox for later processing.
    /// </summary>
    /// <param name="envelope">The CloudEventEnvelope to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
}

