using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for publishing events. Events are automatically wrapped in CloudEventEnvelope format.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event. The event payload is automatically wrapped in a CloudEventEnvelope.
    /// </summary>
    /// <typeparam name="TEvent">The event data type</typeparam>
    /// <param name="payload">The event payload</param>
    /// <param name="subject">Optional subject identifier (e.g., aggregate ID)</param>
    /// <param name="cancellationToken"></param>
    Task PublishAsync<TEvent>(TEvent payload, string? subject = null, CancellationToken cancellationToken = default)
        where TEvent : class;
}
