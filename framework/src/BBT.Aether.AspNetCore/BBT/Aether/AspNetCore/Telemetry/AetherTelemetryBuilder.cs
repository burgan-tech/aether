using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BBT.Aether.AspNetCore.Telemetry;

public sealed class AetherTelemetryBuilder
{
    internal IServiceCollection Services { get; }
    internal List<Action<IServiceCollection, TracerProviderBuilder>> TracingConfigurators { get; } = new();
    internal List<Action<IServiceCollection, MeterProviderBuilder>> MetricsConfigurators { get; } = new();
    internal List<Action<IServiceCollection, OpenTelemetryLoggerOptions>> LoggingConfigurators { get; } = new();

    internal AetherTelemetryBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public AetherTelemetryBuilder ConfigureTracing(Action<IServiceCollection, TracerProviderBuilder> configure)
    {
        TracingConfigurators.Add(configure);
        return this;
    }

    public AetherTelemetryBuilder ConfigureMetrics(Action<IServiceCollection, MeterProviderBuilder> configure)
    {
        MetricsConfigurators.Add(configure);
        return this;
    }

    public AetherTelemetryBuilder ConfigureLogging(Action<IServiceCollection, OpenTelemetryLoggerOptions> configure)
    {
        LoggingConfigurators.Add(configure);
        return this;
    }
}

