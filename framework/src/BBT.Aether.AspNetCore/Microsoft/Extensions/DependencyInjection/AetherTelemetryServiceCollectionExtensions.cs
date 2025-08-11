using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BBT.Aether.AspNetCore.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Filters;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherTelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddFrameworkTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<LoggerConfiguration, TelemetryOptions>? configureLogger = null)
    {
        var options = configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        ConfigureOptions(options, configuration);
        ConfigureTraceProvider(services, options);
        ConfigureLogProvider(services, options, configureLogger);

        return services;
    }

    private static void ConfigureOptions(TelemetryOptions options, IConfiguration configuration)
    {
        if (options.Environment.IsNullOrWhiteSpace())
        {
            options.Environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Development";
        }

        if (options.ServiceName.IsNullOrWhiteSpace())
        {
            options.ServiceName = configuration["OTEL_SERVICE_NAME"] ?? configuration["ApplicationName"];
        }

        if (options.ServiceVersion.IsNullOrWhiteSpace())
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            options.ServiceVersion = FileVersionInfo.GetVersionInfo(assemblyLocation).FileVersion ?? "1.0.0";
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
                            var headersToLog = options.Logging?.Enrichers?.Headers ?? [];
                            activity.DisplayName =
                                $"{httpResponse.HttpContext.Request.Method} {httpResponse.HttpContext.Request.Path.Value ?? "unknown"}";
                            activity.SetTag("http.route", httpResponse.HttpContext.Request.Path.Value ?? "unknown");
                            foreach (var header in headersToLog)
                            {
                                activity.SetTag($"http.request.header.{header.ToLower()}",
                                    httpResponse.HttpContext.Request.Headers.TryGetValue(header, out var headerValue)
                                        ? headerValue.ToString()
                                        : "-");
                            }
                        };

                        b.Filter = httpContext =>
                        {
                            var path = httpContext.Request.Path.Value;

                            if (path == null) return true;

                            foreach (var pattern in options.Logging.ExcludedPaths)
                            {
                                if (Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase))
                                {
                                    return false;
                                }
                            }

                            return true;
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
        if (options.TraceProvider?.ToLower() == "zipkin")
        {
            builder.AddZipkinExporter(zipkinOptions =>
            {
                zipkinOptions.Endpoint = new Uri(options.Zipkin.Endpoint);
            });
        }
        else if (options.TraceProvider?.ToLower() == "otlp")
        {
            builder.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(options.Otlp.Endpoint);
            });
        }

        return builder;
    }

    private static MeterProviderBuilder AddMetricExporter(this MeterProviderBuilder builder, TelemetryOptions options)
    {
        switch (options.TraceProvider.ToLower())
        {
            case "otlp":
                builder.AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(options.Otlp.Endpoint);
                });
                break;

            case "elastic":
                builder.AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(options.Elastic.Endpoint);
                    if (!string.IsNullOrEmpty(options.Elastic.Username))
                    {
                        otlpOptions.Headers =
                            $"Authorization=Bearer {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Elastic.Username}:{options.Elastic.Password}"))}";
                    }
                });
                break;

            case "openobserve":
                builder.AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(options.OpenObserve.Endpoint);
                    if (!string.IsNullOrEmpty(options.OpenObserve.Username))
                    {
                        otlpOptions.Headers =
                            $"Authorization=Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.OpenObserve.Username}:{options.OpenObserve.Password}"))}";
                    }
                });
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

        // Configure file logging
        loggerConfig.WriteTo.File(
            path: options.Logging.FilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 10,
            rollOnFileSizeLimit: true,
            outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");

        // Configure console logging
        loggerConfig.WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");

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

    public static string GetDeploymentId(TelemetryOptions options) =>
        $"{options.Environment}-{options.ServiceName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
}