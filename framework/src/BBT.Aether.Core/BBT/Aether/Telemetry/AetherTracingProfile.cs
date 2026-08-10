using System;
using System.Threading;

namespace BBT.Aether.Telemetry;

/// <summary>
/// Controls the amount of tracing detail produced by Aether instrumentation.
/// </summary>
public enum AetherTracingDetailLevel
{
    /// <summary>
    /// Keeps service boundaries and business spans while suppressing diagnostic detail.
    /// </summary>
    Business = 0,

    /// <summary>
    /// Keeps both business and diagnostic spans.
    /// </summary>
    Verbose = 1
}

/// <summary>
/// Provides the process-wide tracing detail level used by static instrumentation and aspects.
/// </summary>
public static class AetherTracingRuntime
{
    private static int _detailLevel = (int)AetherTracingDetailLevel.Business;

    /// <summary>
    /// Gets the active tracing detail level.
    /// </summary>
    public static AetherTracingDetailLevel DetailLevel =>
        (AetherTracingDetailLevel)Volatile.Read(ref _detailLevel);

    /// <summary>
    /// Gets whether diagnostic spans are enabled.
    /// </summary>
    public static bool IsVerbose => DetailLevel == AetherTracingDetailLevel.Verbose;

    /// <summary>
    /// Configures the process-wide tracing detail level.
    /// </summary>
    public static void Configure(AetherTracingDetailLevel detailLevel)
    {
        if (!Enum.IsDefined(detailLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(detailLevel), detailLevel, "Unsupported tracing detail level.");
        }

        Volatile.Write(ref _detailLevel, (int)detailLevel);
    }
}
