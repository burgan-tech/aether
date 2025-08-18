using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BBT.Aether.AspNetCore.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Filters;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherTelemetryServiceCollectionExtensions
{
    // Performance: Cache compiled regex patterns
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    
    // Performance: Cache base64 encoded credentials
    private static readonly ConcurrentDictionary<string, string> AuthHeaderCache = new();
    public static IServiceCollection AddFrameworkTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<LoggerConfiguration, TelemetryOptions>? configureLogger = null)
    {
        try
        {
            var options = configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
            ConfigureOptions(options, configuration);
            ConfigureTraceProvider(services, options);
            ConfigureLogProvider(services, options, configureLogger);

            return services;
        }
        catch (Exception ex)
        {
            // Log error but don't fail application startup
            try
            {
                using var serviceProvider = services.BuildServiceProvider();
                var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                var logger = loggerFactory?.CreateLogger("AetherTelemetry");
                logger?.LogError(ex, "Failed to configure telemetry. Continuing without telemetry.");
            }
            catch
            {
                // Even logging failed, but don't crash the application
            }
            return services;
        }
    }

    private static void ConfigureOptions(TelemetryOptions options, IConfiguration configuration)
    {
        try
        {
            if (options.Environment.IsNullOrWhiteSpace())
            {
                options.Environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Development";
            }

            if (options.ServiceName.IsNullOrWhiteSpace())
            {
                options.ServiceName = configuration["OTEL_SERVICE_NAME"] ?? configuration["ApplicationName"] ?? "Unknown";
            }

            if (options.ServiceVersion.IsNullOrWhiteSpace())
            {
                try
                {
                    var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(assemblyLocation))
                    {
                        options.ServiceVersion = FileVersionInfo.GetVersionInfo(assemblyLocation).FileVersion ?? "1.0.0";
                    }
                    else
                    {
                        options.ServiceVersion = "1.0.0";
                    }
                }
                catch
                {
                    options.ServiceVersion = "1.0.0";
                }
            }
        }
        catch
        {
            // Ensure we have safe defaults
            options.Environment ??= "Development";
            options.ServiceName ??= "Unknown";
            options.ServiceVersion ??= "1.0.0";
        }
    }

    private static void ConfigureTraceProvider(IServiceCollection services, TelemetryOptions options)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r =>
            {
                r.AddEnvironmentVariableDetector()
                    .AddService(options.ServiceName!)
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["environment"] = options.Environment,
                        ["service.name"] = options.ServiceName!,
                        ["service.version"] = options.ServiceVersion!,
                        ["deployment.id"] = GetDeploymentId(options)
                    });
            })
            .WithTracing(builder =>
            {
                builder
                    .AddSource("Dapr.Client")
                    .AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation(b =>
                    {
                        b.EnrichWithHttpResponse = (activity, httpResponse) =>
                        {
                            try
                            {
                                var headersToLog = options.Logging?.Enrichers?.Headers ?? [];
                                var httpMethod = httpResponse.HttpContext.Request.Method;
                                
                                // Get route pattern for better transaction grouping (with error handling)
                                var routePattern = GetRoutePatternSafe(httpResponse.HttpContext);
                                
                                activity.DisplayName = $"{httpMethod} {routePattern}";
                                activity.SetTag("http.route", routePattern);
                                
                                // Performance: Avoid allocation if no headers to log
                                if (headersToLog.Count > 0)
                                {
                                    foreach (var header in headersToLog)
                                    {
                                        try
                                        {
                                            var headerKey = $"http.request.header.{header.ToLowerInvariant()}";
                                            var headerValue = httpResponse.HttpContext.Request.Headers.TryGetValue(header, out var value)
                                                ? value.ToString()
                                                : "-";
                                            activity.SetTag(headerKey, headerValue);
                                        }
                                        catch
                                        {
                                            // Skip problematic headers
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Don't break telemetry for header processing errors
                            }
                        };

                        b.Filter = httpContext =>
                        {
                            try
                            {
                                var path = httpContext.Request.Path.Value;
                                if (string.IsNullOrEmpty(path)) return true;

                                var excludedPaths = options.Logging?.ExcludedPaths;
                                if (excludedPaths == null || excludedPaths.Count == 0) return true;

                                // Performance: Use cached compiled regex
                                foreach (var pattern in excludedPaths)
                                {
                                    try
                                    {
                                        var regex = RegexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                                        if (regex.IsMatch(path))
                                        {
                                            return false;
                                        }
                                    }
                                    catch
                                    {
                                        // Skip invalid regex patterns
                                        continue;
                                    }
                                }

                                return true;
                            }
                            catch
                            {
                                // Default to including the request if filtering fails
                                return true;
                            }
                        };
                    })
                    .AddEntityFrameworkCoreInstrumentation(efOptions =>
                    {
                        efOptions.SetDbStatementForText = true;
                        efOptions.SetDbStatementForStoredProcedure = true;
                    })
                    .AddTraceExporter(options);
            })
            .WithMetrics(builder =>
            {
                builder
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddMetricExporter(options);
            });
    }

    private static TracerProviderBuilder AddTraceExporter(this TracerProviderBuilder builder, TelemetryOptions options)
    {
        try
        {
            var provider = options.TraceProvider?.ToLowerInvariant();
            
            switch (provider)
            {
                case "zipkin":
                    if (!string.IsNullOrEmpty(options.Zipkin?.Endpoint))
                    {
                        builder.AddZipkinExporter(zipkinOptions =>
                        {
                            if (Uri.TryCreate(options.Zipkin.Endpoint, UriKind.Absolute, out var uri))
                            {
                                zipkinOptions.Endpoint = uri;
                            }
                        });
                    }
                    break;
                    
                case "otlp":
                    if (!string.IsNullOrEmpty(options.Otlp?.Endpoint))
                    {
                        builder.AddOtlpExporter(otlpOptions =>
                        {
                            if (Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out var uri))
                            {
                                otlpOptions.Endpoint = uri;
                            }
                        });
                    }
                    break;
            }
        }
        catch
        {
            // Continue without trace exporter if configuration fails
        }

        return builder;
    }

    private static MeterProviderBuilder AddMetricExporter(this MeterProviderBuilder builder, TelemetryOptions options)
    {
        var provider = options.TraceProvider;
        
        switch (provider?.ToLower())
        {
            case "otlp":
            case "elastic":
            case "openobserve":
                // All these providers use OTLP protocol
                // Configuration can be done via environment variables (OTEL_EXPORTER_OTLP_METRICS_*)
                // or via code configuration below
                builder.AddOtlpExporter(otlpOptions =>
                {
                    // Only set if not already configured via environment variables
                    if (otlpOptions.Endpoint == null)
                    {
                        var endpoint = provider switch
                        {
                            "otlp" => options.Otlp?.Endpoint,
                            "elastic" => options.Elastic?.Endpoint,
                            "openobserve" => options.OpenObserve?.Endpoint,
                            _ => null
                        };
                        
                        if (!string.IsNullOrEmpty(endpoint))
                        {
                            otlpOptions.Endpoint = new Uri(endpoint);
                        }
                    }
                    
                    // Set default protocol if not configured via env vars
                    if (otlpOptions.Protocol == OpenTelemetry.Exporter.OtlpExportProtocol.Grpc)
                    {
                        otlpOptions.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    }
                    
                    // Set auth headers if not configured via env vars and credentials available
                    if (string.IsNullOrEmpty(otlpOptions.Headers))
                    {
                        var authHeader = provider switch
                        {
                            "elastic" when !string.IsNullOrEmpty(options.Elastic?.Username) =>
                                GetCachedAuthHeader($"elastic:{options.Elastic.Username}:{options.Elastic.Password}"),
                            "openobserve" when !string.IsNullOrEmpty(options.OpenObserve?.Username) =>
                                GetCachedAuthHeader($"openobserve:{options.OpenObserve.Username}:{options.OpenObserve.Password}"),
                            _ => null
                        };
                        
                        if (!string.IsNullOrEmpty(authHeader))
                        {
                            otlpOptions.Headers = authHeader;
                        }
                    }
                });
                break;
                
            default:
                // No metric exporter configured
                break;
        }

        return builder;
    }

    private static void ConfigureLogProvider(IServiceCollection services, TelemetryOptions options,
        Action<LoggerConfiguration, TelemetryOptions>? configureLogger = null)
    {
        if (!options.Logging.Enabled)
            return;

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(ConvertLogLevel(options.Logging.MinimumLevel))
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Environment", options.Environment)
            .Enrich.WithProperty("ServiceName", options.ServiceName)
            .Enrich.WithProperty("ServiceVersion", options.ServiceVersion)
            .Enrich.WithProperty("DeploymentId", GetDeploymentId(options))
            .Filter.ByExcluding(Matching.FromSource("Microsoft.AspNetCore.StaticFiles"));

        if (options.LogProvider.Contains("file", StringComparison.OrdinalIgnoreCase))
        {
            // Configure file logging
            loggerConfig.WriteTo.File(
                path: options.Logging.FilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                rollOnFileSizeLimit: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
        }

        if (options.LogProvider.Contains("console", StringComparison.OrdinalIgnoreCase))
        {
            // Configure console logging
            loggerConfig.WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
        }

        if (options.LogProvider.Contains("otlp", StringComparison.OrdinalIgnoreCase))
        {
            loggerConfig.WriteTo.OpenTelemetry();
        }
        
        configureLogger?.Invoke(loggerConfig, options);

        Log.Logger = loggerConfig.CreateLogger();

        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddSerilog(dispose: true);
        });
    }

    private static LogEventLevel ConvertLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

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
    
    private static string GetCachedAuthHeader(string key)
    {
        return AuthHeaderCache.GetOrAdd(key, k =>
        {
            try
            {
                var parts = k.Split(':');
                if (parts.Length >= 3)
                {
                    var username = parts[1];
                    var password = parts[2];
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    return $"Authorization=Basic {credentials}";
                }
            }
            catch
            {
                // Return empty if encoding fails
            }
            return string.Empty;
        });
    }

    public static string GetDeploymentId(TelemetryOptions options)
    {
        return $"{options.Environment}-{options.ServiceName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
    }
}