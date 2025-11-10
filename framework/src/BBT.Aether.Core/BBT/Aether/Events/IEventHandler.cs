using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Unified handler interface for distributed events.
/// All event handlers must implement this interface.
/// Handlers receive strongly-typed CloudEventEnvelope&lt;T&gt; with event data and metadata.
/// </summary>
/// <typeparam name="T">The event data type</typeparam>
public interface IEventHandler<T>
{
    /// <summary>
    /// Handles a distributed event wrapped in a CloudEvent envelope.
    /// </summary>
    /// <param name="envelope">The strongly-typed CloudEvent envelope containing event data and metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task HandleAsync(CloudEventEnvelope<T> envelope, CancellationToken cancellationToken);
}

