using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for distributed event publishing. Events are automatically wrapped in CloudEventEnvelope format.
/// </summary>
public interface IDistributedEventBus : IEventBus
{
    /// <summary>
    /// Publishes an event with outbox support. The event payload is automatically wrapped in a CloudEventEnvelope.
    /// </summary>
    /// <typeparam name="TEvent">The event data type</typeparam>
    /// <param name="payload">The event payload</param>
    /// <param name="subject">Optional subject identifier (e.g., aggregate ID)</param>
    /// <param name="useOutbox">Whether to use the outbox pattern for transactional publishing</param>
    /// <param name="cancellationToken"></param>
    Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        bool useOutbox = true, 
        CancellationToken cancellationToken = default)
        where TEvent : class;
}
