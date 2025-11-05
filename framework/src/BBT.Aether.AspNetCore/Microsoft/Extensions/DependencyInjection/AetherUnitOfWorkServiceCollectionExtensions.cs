using System;
using BBT.Aether.AspNetCore.Middleware;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for IServiceCollection to register Unit of Work middleware services.
/// </summary>
public static class AetherUnitOfWorkServiceCollectionExtensions
{
    /// <summary>
    /// Registers Unit of Work middleware with optional configuration.
    /// Call this in ConfigureServices/Program.cs before building the app.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional action to configure middleware options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherUnitOfWorkMiddleware(
        this IServiceCollection services,
        Action<UnitOfWorkMiddlewareOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddTransient<UnitOfWorkMiddleware>();
        return services;
    }
}

