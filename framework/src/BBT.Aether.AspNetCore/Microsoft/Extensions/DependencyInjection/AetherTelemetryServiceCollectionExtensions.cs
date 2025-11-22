using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BBT.Aether.AspNetCore.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherTelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddAetherTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null,
        Action<AetherTelemetryBuilder>? configure = null)
    {
        // Bind and configure options upfront to avoid anti-pattern
        var opts = new AetherTelemetryOptions();

        // Try Telemetry section first, then fallback to Aether:Telemetry
        var section = configuration.GetSection(AetherTelemetryOptions.SectionName);
        if (!section.Exists())
        {
            section = configuration.GetSection("Telemetry");
        }

        section.Bind(opts);

        // Apply defaults and environment variables
        var envName =
            environment?.EnvironmentName ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            "Production";

        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var serviceVersion = FileVersionInfo.GetVersionInfo(assemblyLocation).FileVersion ?? "1.0.0";
        // Service name: OTEL_SERVICE_NAME > config > entry assembly name > "aether"
        opts.ServiceName ??=
            Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? configuration["ApplicationName"]
            ?? "aether";

        // Service version: config > entry assembly version > "1.0.0"
        opts.ServiceVersion ??= serviceVersion;

        // OTLP endpoint: OTEL_EXPORTER_OTLP_ENDPOINT > config > "http://localhost:4318"
        opts.Otlp.Endpoint ??=
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            ?? "http://localhost:4318";

        // OTLP protocol: OTEL_EXPORTER_OTLP_PROTOCOL > config
        opts.Otlp.Protocol =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")
            ?? opts.Otlp.Protocol;

        opts.ServiceNamespace ??= "aether";

        // Store environment in custom attributes for deployment tracking
        opts.Logging.Enrichers.CustomAttributes.TryAdd("deployment.environment", envName);

        // Register options for DI consumers
        services.AddSingleton<IOptions<AetherTelemetryOptions>>(new OptionsWrapper<AetherTelemetryOptions>(opts));

        var builder = new AetherTelemetryBuilder(services);
        configure?.Invoke(builder);

        // OpenTelemetry: Tracing & Metrics
        services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource
                    .AddService(
                        serviceName: opts.ServiceName!,
                        serviceVersion: opts.ServiceVersion,
                        serviceInstanceId: Environment.MachineName)
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["deployment.id"] = GetDeploymentId(opts, envName),
                        ["service.namespace"] = opts.ServiceNamespace ?? "aether",
                        ["deployment.environment"] =
                            opts.Logging.Enrichers.CustomAttributes.GetValueOrDefault("deployment.environment", "Production")
                    });
            })
            .WithTracing(tracing =>
            {
                if (!opts.TracingEnabled) return;

                var excludedPatterns = CompileRegex(opts.Logging.ExcludedPaths
                    .Concat(opts.Tracing.ExcludedPaths));

                // Common instrumentation
                if (opts.Tracing.EnableAspNetCore)
                {
                    tracing.AddAspNetCoreInstrumentation(o =>
                        {
                        o.Filter = ctx => !IsExcluded(ctx.Request.Path.Value, excludedPatterns);

                        o.EnrichWithHttpRequest = (activity, request) =>
                        {
                            try
                            {
                                // Add headers
                                foreach (var header in opts.Logging.Enrichers.Headers)
                                {
                                    if (request.Headers.TryGetValue(header, out var value))
                                    {
                                        var headerKey = $"http.request.header.{header.ToLowerInvariant()}";
                                        activity.SetTag(headerKey, value.ToString());
                                    }
                                }

                                // Add custom attributes
                                foreach (var attr in opts.Logging.Enrichers.CustomAttributes)
                                {
                                    activity.SetTag(attr.Key, attr.Value);
                                }
                            }
                            catch
                            {
                                // Skip problematic enrichment
                            }
                        };

                        o.EnrichWithHttpResponse = (activity, response) =>
                        {
                            try
                            {
                                // Get route pattern for better transaction grouping
                                var routePattern = GetRoutePatternSafe(response.HttpContext);
                                var httpMethod = response.HttpContext.Request.Method;

                                activity.DisplayName = $"{httpMethod} {routePattern}";
                                activity.SetTag("http.route", routePattern);
                                activity.SetTag("http.response.content_length", response.ContentLength);
                            }
                            catch
                            {
                                // Skip problematic enrichment
                            }
                        };
                    });
                }

                if (opts.Tracing.EnableHttpClient)
                {
                    tracing.AddHttpClientInstrumentation(o =>
                    {
                        o.FilterHttpRequestMessage = req =>
                            !IsExcluded(req.RequestUri?.ToString(), excludedPatterns);
                    });
                }

                if (opts.Tracing.EnableEntityFrameworkCore)
                {
                    tracing.AddEntityFrameworkCoreInstrumentation(efOptions =>
                    {
                        efOptions.SetDbStatementForText = true;
                        efOptions.SetDbStatementForStoredProcedure = true;
                    });
                }
                
                tracing.AddSource("BBT.Aether.Aspects");
                
                // Custom sources
                foreach (var src in opts.Tracing.AdditionalSources)
                {
                    tracing.AddSource(src);
                }

                // Project/consumer hooks (using lazy service provider access)
                foreach (var cfg in builder.TracingConfigurators)
                {
                    cfg(services, tracing);
                }

                // Exporters
                if (opts.Tracing.EnableConsoleExporter)
                    tracing.AddConsoleExporter();

                if (opts.Tracing.EnableOtlpExporter)
                    tracing.AddOtlpExporter(o =>
                        OtlpExporterConfigurator.Configure(o, opts.Otlp, "/v1/traces"));
            })
            .WithMetrics(metrics =>
            {
                if (!opts.MetricsEnabled) return;

                if (opts.Metrics.EnableAspNetCore)
                    metrics.AddAspNetCoreInstrumentation();

                if (opts.Metrics.EnableHttpClient)
                    metrics.AddHttpClientInstrumentation();

                if (opts.Metrics.EnableRuntime)
                    metrics.AddRuntimeInstrumentation();

                if (opts.Metrics.EnableProcess)
                    metrics.AddProcessInstrumentation();
                
                metrics.AddMeter("BBT.Aether.Aspects");

                foreach (var m in opts.Metrics.AdditionalMeters)
                {
                    metrics.AddMeter(m);
                }

                foreach (var cfg in builder.MetricsConfigurators)
                {
                    cfg(services, metrics);
                }

                if (opts.Metrics.EnableConsoleExporter)
                    metrics.AddConsoleExporter();

                if (opts.Metrics.EnableOtlpExporter)
                    metrics.AddOtlpExporter(o =>
                        OtlpExporterConfigurator.Configure(o, opts.Otlp, "/v1/metrics"));
            });

        // Logging
        services.AddLogging(lb =>
        {
            lb.AddOpenTelemetry(logging =>
            {
                if (!opts.LoggingEnabled) return;

                var excludedPatterns = CompileRegex(opts.Logging.ExcludedPaths);

                logging.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(opts.ServiceName!, opts.ServiceNamespace ?? "aether", opts.ServiceVersion ?? "1.0.0", false, Environment.MachineName)
                    .AddAttributes(opts.Logging.Enrichers.CustomAttributes
                        .ToDictionary(x => x.Key, x => (object)x.Value)));

                // Enricher processor
                logging.AddProcessor(provider =>
                {
                    var accessor = provider.GetService<IHttpContextAccessor>();
                    return new AetherLogEnricherProcessor(opts, excludedPatterns, accessor);
                });

                logging.IncludeFormattedMessage = opts.Logging.IncludeFormattedMessage;
                logging.IncludeScopes = opts.Logging.IncludeScopes;
                logging.ParseStateValues = opts.Logging.ParseStateValues;

                foreach (var cfg in builder.LoggingConfigurators)
                {
                    cfg(services, logging);
                }

                if (opts.Logging.EnableConsoleExporter)
                    logging.AddConsoleExporter();

                if (opts.Logging.EnableOtlpExporter)
                    logging.AddOtlpExporter(o =>
                        OtlpExporterConfigurator.Configure(o, opts.Otlp, "/v1/logs"));
            });
        });

        return services;
    }

    private static List<Regex> CompileRegex(IEnumerable<string> patterns)
        => patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToList();

    private static bool IsExcluded(string? value, List<Regex> patterns)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(value))
                {
                    return true;
                }
            }
            catch
            {
                // Skip invalid patterns
            }
        }

        return false;
    }

    private static string GetRoutePatternSafe(HttpContext httpContext)
    {
        try
        {
            var routePattern = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            return routePattern ?? httpContext.Request.Path.Value ?? "unknown";
        }
        catch
        {
            return httpContext.Request.Path.Value ?? "unknown";
        }
    }
    
    public static string GetDeploymentId(AetherTelemetryOptions options, string environment)
    {
        return $"{environment}-{options.ServiceName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
    }
}