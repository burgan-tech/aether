using BBT.Aether.AspNetCore.Middleware;
using BBT.Aether.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for IApplicationBuilder to configure Aether aspects.
/// </summary>
public static class AetherAspectApplicationBuilderExtensions
{
    /// <summary>
    /// Adds middleware to set AmbientServiceProvider for each request.
    /// This enables PostSharp aspects to access DI services.
    /// Must be called after building the app but before UseRouting/UseEndpoints.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseAetherAmbientServiceProvider(this IApplicationBuilder app)
    {
        // Set root service provider for fallback
        AmbientServiceProvider.Root = app.ApplicationServices;

        // Add middleware to set request-scoped service provider
        app.UseMiddleware<AmbientServiceProviderMiddleware>();

        return app;
    }
}

