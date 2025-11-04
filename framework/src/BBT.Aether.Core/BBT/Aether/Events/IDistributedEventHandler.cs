using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines a handler for distributed events with strongly-typed access to event data.
/// </summary>
/// <typeparam name="TEvent">The type of event to handle</typeparam>
public interface IDistributedEventHandler<TEvent>
{
    /// <summary>
    /// Handles a distributed event wrapped in a CloudEvent envelope.
    /// </summary>
    /// <param name="envelope">The strongly-typed CloudEvent envelope containing event data and metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task HandleAsync(CloudEventEnvelope<TEvent> envelope, CancellationToken cancellationToken);
}
