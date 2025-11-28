using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Generic implementation of IBackgroundJobInvoker.
/// Type parameter TArgs is closed at registration time (startup), not at runtime.
/// This eliminates runtime reflection for handler invocation.
/// </summary>
/// <typeparam name="TArgs">The type of job arguments expected by the handler</typeparam>
internal sealed class BackgroundJobInvoker<TArgs> : IBackgroundJobInvoker
{
    public async Task InvokeAsync(
        IServiceScopeFactory scopeFactory,
        IEventSerializer eventSerializer,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        // Resolve dependencies from DI
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IBackgroundJobHandler<TArgs>>();

        var args = eventSerializer.Deserialize<TArgs>(payload.Span);
        if (args == null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize job payload to {typeof(TArgs).Name}");
        }

        // Invoke handler - completely type-safe, no reflection
        await handler.HandleAsync(args, cancellationToken);
    }
}

