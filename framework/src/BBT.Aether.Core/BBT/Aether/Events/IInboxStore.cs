using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for the inbox store, used for event idempotency checking.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Checks if an event with the specified ID has already been processed.
    /// </summary>
    /// <param name="eventId">The unique event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the event has been processed, false otherwise</returns>
    Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an event as processed by storing the CloudEventEnvelope.
    /// </summary>
    /// <param name="envelope">The CloudEventEnvelope that was processed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkProcessedAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
}

