using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Invokes the handler by resolving from DI and calling HandleAsync.
    /// Completely type-safe - no reflection at runtime.
    /// </summary>
    public async Task InvokeAsync(
        IServiceProvider serviceProvider,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        // Resolve handler from DI - handler already registered at startup
        var handler = serviceProvider.GetRequiredService<IBackgroundJobHandler<TArgs>>();

        // Deserialize payload to strongly-typed TArgs
        var args = JsonSerializer.Deserialize<TArgs>(payload.Span);
        if (args == null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize job payload to {typeof(TArgs).Name}");
        }

        // Invoke handler - completely type-safe, no reflection
        await handler.HandleAsync(args, cancellationToken);
    }
}

