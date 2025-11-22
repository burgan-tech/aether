using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Non-generic interface for invoking background job handlers.
/// Hides generic TArgs behind an abstraction, allowing type-safe invocation without runtime reflection.
/// </summary>
public interface IBackgroundJobInvoker
{
    /// <summary>
    /// Invokes the job handler with the serialized payload.
    /// Deserializes payload to TArgs and calls handler.HandleAsync().
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving handler instances</param>
    /// <param name="payload">Serialized job payload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task InvokeAsync(
        IServiceProvider serviceProvider,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

