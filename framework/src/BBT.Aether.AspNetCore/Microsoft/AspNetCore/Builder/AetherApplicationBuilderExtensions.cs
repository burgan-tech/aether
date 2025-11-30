using BBT.Aether.AspNetCore.MultiSchema;
using BBT.Aether.AspNetCore.ResponseCompression;
using BBT.Aether.AspNetCore.Security;
using BBT.Aether.AspNetCore.Tracing;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

public static class AetherApplicationBuilderExtensions
{
    public static IApplicationBuilder UseCurrentUser(this IApplicationBuilder app)
    {
        return app
            .UseMiddleware<AetherCurrentUserMiddleware>();
    }

    /// <summary>
    /// Adds correlation ID and trace context middleware to the application pipeline.
    /// This middleware handles both correlation ID and OpenTelemetry trace context headers.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app
            .UseMiddleware<AetherCorrelationIdMiddleware>();
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AetherSecurityHeadersMiddleware>();
    }
    
    /// <summary>
    /// Adds schema resolution middleware to the application pipeline.
    /// This middleware resolves the current schema from HTTP requests (header, query string, or route).
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for method chaining.</returns>
    /// <remarks>
    /// <para><strong>Pipeline Order:</strong></para>
    /// <list type="number">
    /// <item><description>UseRouting() - MUST come first if using route-based schema resolution</description></item>
    /// <item><description>UseAuthentication() / UseAuthorization() - If using JWT claim-based custom strategies</description></item>
    /// <item><description>UseSchemaResolution() - Place here to resolve schema before business logic</description></item>
    /// <item><description>UseUnitOfWorkMiddleware() - After schema resolution</description></item>
    /// <item><description>MapControllers() / UseEndpoints() - At the end</description></item>
    /// </list>
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// app.UseRouting();
    /// app.UseAuthentication();
    /// app.UseSchemaResolution();
    /// app.UseUnitOfWorkMiddleware();
    /// app.MapControllers();
    /// </code>
    /// <para>Configure schema resolution options using <c>AddSchemaResolution()</c> in your service registration.</para>
    /// </remarks>
    public static IApplicationBuilder UseSchemaResolution(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SchemaResolutionMiddleware>();
    }

    public static IApplicationBuilder UseAppResponseCompression(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<IOptions<ResponseCompressionOptions>>();
        if (options?.Value is { Enable: true })
        {
            return app.UseResponseCompression();
        }

        return app;
    }

    public static IApplicationBuilder UseAetherApiVersioning(
        this IApplicationBuilder app,
        bool useSwagger = true,
        bool useSwaggerUi = true)
    {
        // Use API Versioning
        app.UseApiVersioning();

        // Configure Swagger and SwaggerUI if enabled
        if (useSwagger)
        {
            app.UseSwagger();
        }

        if (useSwaggerUi)
        {
            app.UseSwaggerUI(options =>
            {
                var apiVersionDescriptionProvider = app.ApplicationServices
                    .GetRequiredService<IApiVersionDescriptionProvider>();

                foreach (var description in apiVersionDescriptionProvider.ApiVersionDescriptions)
                {
                    options.SwaggerEndpoint(
                        $"/swagger/{description.GroupName}/swagger.json",
                        description.GroupName.ToUpperInvariant()
                    );
                }
            });
        }

        return app;
    }
}