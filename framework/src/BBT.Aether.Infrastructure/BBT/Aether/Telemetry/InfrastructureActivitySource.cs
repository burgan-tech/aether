using System.Diagnostics;

namespace BBT.Aether.Telemetry;

/// <summary>
/// Provides a static ActivitySource for Aether infrastructure tracing (distributed lock, cache, etc.).
/// Automatically registered by <c>AddAetherTelemetry</c>; no manual configuration required.
/// </summary>
public static class InfrastructureActivitySource
{
    /// <summary>
    /// The name of the ActivitySource used by Aether infrastructure.
    /// </summary>
    public const string SourceName = "BBT.Aether.Infrastructure";

    /// <summary>
    /// The version of the Aether infrastructure library.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The shared ActivitySource instance for creating activities (spans) in Aether infrastructure.
    /// </summary>
    public static readonly ActivitySource Source = new(SourceName, Version);

    /// <summary>
    /// Starts an infrastructure diagnostic span only when the global tracing profile is Verbose.
    /// </summary>
    public static Activity? StartDiagnosticActivity(
        string operationName,
        ActivityKind kind,
        ActivityContext parentContext = default)
    {
        return AetherTracingRuntime.IsVerbose
            ? Source.StartActivity(operationName, kind, parentContext)
            : null;
    }
}
