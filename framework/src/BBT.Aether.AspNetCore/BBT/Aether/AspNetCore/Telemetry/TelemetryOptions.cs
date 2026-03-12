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

    /// <summary>
    /// Options for HTTP request/response body logging (logging channel only; no trace tags).
    /// </summary>
    public HttpBodyLoggingOptions Body { get; set; } = new();
}

/// <summary>
/// Options for capturing and logging HTTP request/response bodies. Path exclusion uses <see cref="AetherLoggingOptions.ExcludedPaths"/>.
/// </summary>
public sealed class HttpBodyLoggingOptions
{
    /// <summary>
    /// Built-in default JSON property names that are redacted in request/response body logs.
    /// Effective list is this plus <see cref="AdditionalSensitiveJsonFields"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSensitiveJsonFields = new[]
    {
        "password", "token", "access_token", "refresh_token", "client_secret", "secret",
        "otp", "pin", "iban", "cardNumber", "pan", "cvv"
    };

    /// <summary>
    /// Built-in default header names that are redacted in logs. Effective list is this plus <see cref="AdditionalSensitiveHeaderNames"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSensitiveHeaderNames = new[]
    {
        "Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "X-Auth-Token"
    };

    public bool EnableRequestBody { get; set; } = false;
    public bool EnableResponseBody { get; set; } = false;

    public int MaxBodyLengthToCapture { get; set; } = 16 * 1024;

    public List<string> AllowedContentTypes { get; set; } = new()
    {
        "application/json",
        "application/problem+json",
        "application/xml",
        "text/plain",
        "text/xml"
    };

    /// <summary>
    /// Additional JSON property names to redact (merged with <see cref="DefaultSensitiveJsonFields"/>).
    /// </summary>
    public List<string> AdditionalSensitiveJsonFields { get; set; } = new();

    /// <summary>
    /// Additional header names to redact (merged with <see cref="DefaultSensitiveHeaderNames"/>).
    /// </summary>
    public List<string> AdditionalSensitiveHeaderNames { get; set; } = new();
}

/// <summary>
/// Options for enriching HTTP body log events (middleware). CustomAttributes and listed headers are added as scope properties; sensitive headers are redacted.
/// </summary>
public sealed class LoggingEnricherOptions
{
    /// <summary>
    /// Key-value pairs added to every HTTP body log event as enrich properties.
    /// </summary>
    public Dictionary<string, string> CustomAttributes { get; set; } = new();

    /// <summary>
    /// Header names to add as individual enrich properties (e.g. RequestHeader.x_correlation_id). Values for headers in the sensitive list are shown as ***REDACTED***. Full request/response headers remain in RequestHeaders/ResponseHeaders JSON.
    /// </summary>
    public List<string> Headers { get; set; } = new();
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

    /// <summary>
    /// Request header names to add as activity tags on ASP.NET Core spans. Only explicitly listed headers are added; no wildcard.
    /// </summary>
    public List<string> Headers { get; set; } = new();
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
