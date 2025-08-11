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

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app
            .UseMiddleware<AetherCorrelationIdMiddleware>();
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AetherSecurityHeadersMiddleware>();
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