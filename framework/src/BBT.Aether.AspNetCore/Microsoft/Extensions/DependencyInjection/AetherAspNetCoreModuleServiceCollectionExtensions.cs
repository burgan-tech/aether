using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.AspNetCore.Security;
using BBT.Aether.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherAspNetCoreModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAetherAspNetCore(
        this IServiceCollection services,
        Action<IServiceCollection>? configureServices = null)
    {
        configureServices?.Invoke(services);

        var configuration = services.GetConfiguration();

        services.AddHttpContextAccessor();

        //Exception Handler
        services.AddTransient<IHttpExceptionStatusCodeFinder, DefaultHttpExceptionStatusCodeFinder>();
        services.AddExceptionHandler();
        //Header Current User
        services.AddTransient<HeaderCurrentUserResolver>();

        //Middleware
        services.AddTransient<AetherCurrentUserMiddleware>();
        services.AddTransient<AetherCorrelationIdMiddleware>();
        services.AddTransient<AetherSecurityHeadersMiddleware>();

        //ResponseCompression
        services.AddResponseCompression(configuration);

        return services;
    }

    private static IServiceCollection AddExceptionHandler(this IServiceCollection services)
    {
        services.AddExceptionHandler<AetherExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }

    private static IServiceCollection AddResponseCompression(this IServiceCollection services,
        IConfiguration configuration)
    {
        var responseCompressionConfig = configuration.GetSection("ResponseCompression");
        services.Configure<BBT.Aether.AspNetCore.ResponseCompression.ResponseCompressionOptions>(responseCompressionConfig);
        var enableCompression = responseCompressionConfig.GetValue<bool>("Enable");

        if (!enableCompression)
        {
            return services;
        }

        var mimeTypes = responseCompressionConfig.GetSection("MimeTypes").Get<string[]>() ?? [];
        var excludedMimeTypes = responseCompressionConfig.GetSection("ExcludedMimeTypes").Get<string[]>() ?? [];
        var providers = responseCompressionConfig.GetSection("Providers").Get<string[]>() ?? [];
        
        services.AddResponseCompression(options =>
        {
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(mimeTypes);
            options.ExcludedMimeTypes = excludedMimeTypes;
            options.EnableForHttps = responseCompressionConfig.GetValue<bool>("EnableForHttps", true);
            foreach (var provider in providers)
            {
                switch (provider.ToLowerInvariant())
                {
                    case "gzip":
                        options.Providers.Add<GzipCompressionProvider>();
                        break;
                    case "brotli":
                        options.Providers.Add<BrotliCompressionProvider>();
                        break;
                }
            }
        });

        if (providers.Contains("gzip", StringComparer.OrdinalIgnoreCase))
        {
            services.Configure<GzipCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Optimal;
            });
        }

        if (providers.Contains("brotli", StringComparer.OrdinalIgnoreCase))
        {
            services.Configure<BrotliCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Optimal;
            });
        }

        return services;
    }
}