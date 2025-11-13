using BBT.Aether.AspNetCore.Middleware;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for IApplicationBuilder to configure Unit of Work middleware.
/// </summary>
public static class AetherUnitOfWorkApplicationBuilderExtensions
{
    /// <summary>
    /// Adds Unit of Work middleware to the pipeline.
    /// This middleware automatically manages UoW for HTTP requests based on configuration.
    /// Should be called early in the pipeline, before UseRouting/UseEndpoints.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseAetherUnitOfWork(this IApplicationBuilder app)
    {
        app.UseMiddleware<UnitOfWorkMiddleware>();
        return app;
    }
}

