using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.OpenApi.Models;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherApiVersioningServiceCollectionExtensions
{
    public static IServiceCollection AddAetherApiVersioning(
        this IServiceCollection services,
        string apiTitle = "API")
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });

        services.AddVersionedApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        // Explicitly register the ApiVersionDescriptionProvider
        services.AddSingleton<IApiVersionDescriptionProvider, DefaultApiVersionDescriptionProvider>();

        // Configure Swagger with versioning support
        services.AddSwaggerGen(options =>
        {
            // Add a swagger document for each discovered API version
            var provider = services.BuildServiceProvider().GetRequiredService<IApiVersionDescriptionProvider>();

            foreach (var description in provider.ApiVersionDescriptions)
            {
                options.SwaggerDoc(description.GroupName, new OpenApiInfo()
                {
                    Title = apiTitle,
                    Version = description.ApiVersion.ToString(),
                    Description = description.IsDeprecated
                        ? "This API version has been deprecated."
                        : "API endpoints"
                });
            }
        });

        return services;
    }
}