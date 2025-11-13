using BBT.Aether.AspNetCore.Middleware;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for IServiceCollection to register Aether aspect services.
/// </summary>
public static class AetherAspectServiceCollectionExtensions
{
    /// <summary>
    /// Registers services required for Aether aspects (AmbientServiceProvider middleware).
    /// Call this in ConfigureServices/Program.cs before building the app.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherAmbientServiceProvider(this IServiceCollection services)
    {
        services.AddTransient<AmbientServiceProviderMiddleware>();
        return services;
    }
}

