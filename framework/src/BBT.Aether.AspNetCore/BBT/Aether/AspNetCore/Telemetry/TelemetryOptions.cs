using System.Collections.Generic;

namespace BBT.Aether.AspNetCore.Telemetry;

public sealed class AetherTelemetryOptions
{
    public const string SectionName = "Telemetry";

    // Service identity
    public string? ServiceName { get; set; }
    public string? ServiceNamespace { get; set; } = "aether";
    public string? ServiceVersion { get; set; }

    // Feature toggles
    public bool TracingEnabled { get; set; } = true;
    public bool MetricsEnabled { get; set; } = true;
    public bool LoggingEnabled { get; set; } = true;

    // OTLP
    public OtlpOptions Otlp { get; set; } = new();
    public AetherLoggingOptions Logging { get; set; } = new();
    public AetherTracingOptions Tracing { get; set; } = new();
    public AetherMetricsOptions Metrics { get; set; } = new();
}

public sealed class OtlpOptions
{
    public string? Endpoint { get; set; } // e.g. http://otel-collector:4318
    public string Protocol { get; set; } = "http/protobuf"; // or "grpc"
}

public sealed class AetherLoggingOptions
{
    public bool EnableConsoleExporter { get; set; } = false;
    public bool EnableOtlpExporter { get; set; } = true;

    public bool IncludeFormattedMessage { get; set; } = true;
    public bool IncludeScopes { get; set; } = false;
    public bool ParseStateValues { get; set; } = true;

    public List<string> ExcludedPaths { get; set; } = new();
    public LoggingEnricherOptions Enrichers { get; set; } = new();
}

public sealed class LoggingEnricherOptions
{
    public List<string> Headers { get; set; } = new();
    public Dictionary<string, string> CustomAttributes { get; set; } = new();
}

public sealed class AetherTracingOptions
{
    public bool EnableAspNetCore { get; set; } = true;
    public bool EnableHttpClient { get; set; } = true;
    public bool EnableEntityFrameworkCore { get; set; } = true;
    public bool EnableRuntimeSources { get; set; } = true;

    public bool EnableConsoleExporter { get; set; } = false;
    public bool EnableOtlpExporter { get; set; } = true;

    public List<string> ExcludedPaths { get; set; } = new();
    public List<string> AdditionalSources { get; set; } = new(); // projects can add their own ActivitySource names
}

public sealed class AetherMetricsOptions
{
    public bool EnableAspNetCore { get; set; } = true;
    public bool EnableHttpClient { get; set; } = true;
    public bool EnableRuntime { get; set; } = true;
    public bool EnableProcess { get; set; } = true;

    public bool EnableConsoleExporter { get; set; } = false;
    public bool EnableOtlpExporter { get; set; } = true;

    public List<string> AdditionalMeters { get; set; } = new();
}
