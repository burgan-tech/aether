using System.Diagnostics;

namespace BBT.Aether.Aspects;

/// <summary>
/// Provides a static ActivitySource for Aether aspect tracing.
/// This ActivitySource should be registered in the OpenTelemetry tracing configuration:
/// <code>
/// services.AddAetherTelemetry(configuration, environment, telemetry =>
/// {
///     telemetry.ConfigureTracing((sp, tracing) =>
///     {
///         tracing.AddSource("BBT.Aether.Aspects");
///     });
/// });
/// </code>
/// </summary>
public static class AetherActivitySource
{
    /// <summary>
    /// The name of the ActivitySource used by Aether aspects.
    /// </summary>
    public const string SourceName = "BBT.Aether.Aspects";

    /// <summary>
    /// The version of the Aether aspects library.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The shared ActivitySource instance for creating activities (spans) in Aether aspects.
    /// </summary>
    public readonly static ActivitySource Source = new(SourceName, Version);
}

