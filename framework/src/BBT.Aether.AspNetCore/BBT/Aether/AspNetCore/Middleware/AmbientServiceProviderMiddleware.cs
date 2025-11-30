using System;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Middleware;

/// <summary>
/// Middleware that sets the ambient service provider for the current request.
/// This enables aspects and other cross-cutting concerns to access DI services.
/// </summary>
public sealed class AmbientServiceProviderMiddleware : IMiddleware
{
    private readonly IServiceProvider _root;

    /// <summary>
    /// Initializes a new instance of AmbientServiceProviderMiddleware.
    /// </summary>
    /// <param name="root">Root service provider used as fallback</param>
    public AmbientServiceProviderMiddleware(IServiceProvider root)
    {
        _root = root;
    }

    /// <summary>
    /// Invokes the middleware to set ambient service provider for the request scope.
    /// </summary>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var previousCurrent = AmbientServiceProvider.Current;

        try
        {
            // Set request-scoped service provider as ambient
            AmbientServiceProvider.Current = context.RequestServices ?? _root;

            await next(context);
        }
        finally
        {
            // Restore previous ambient context
            AmbientServiceProvider.Current = previousCurrent;
        }
    }
}

