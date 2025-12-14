using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using Microsoft.Extensions.DependencyInjection;

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
    /// <param name="scopeFactory">Scope factory for creating a scope for resolving handler instances</param>
    /// <param name="eventSerializer">Event serializer for deserializing payload</param>
    /// <param name="payload">Serialized job payload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task InvokeAsync(
        IServiceProvider scopeFactory,
        IEventSerializer eventSerializer,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

