using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace BBT.Aether.Aspects;

/// <summary>
/// Provides a static Meter for Aether aspect metrics with instrument caching.
/// This Meter should be registered in the OpenTelemetry metrics configuration:
/// <code>
/// services.AddAetherTelemetry(configuration, environment, telemetry =>
/// {
///     telemetry.ConfigureMetrics((sp, metrics) =>
///     {
///         metrics.AddMeter("BBT.Aether.Aspects");
///     });
/// });
/// </code>
/// </summary>
public static class AetherMeter
{
    /// <summary>
    /// The name of the Meter used by Aether aspects.
    /// </summary>
    public const string MeterName = "BBT.Aether.Aspects";

    /// <summary>
    /// The version of the Aether aspects library.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The shared Meter instance for creating metric instruments in Aether aspects.
    /// </summary>
    public static readonly Meter Instance = new(MeterName, Version);

    // Instrument caches to avoid repeated creation
    private static readonly ConcurrentDictionary<string, Histogram<double>> HistogramCache = new();
    private static readonly ConcurrentDictionary<string, Counter<long>> CounterCache = new();
    private static readonly ConcurrentDictionary<string, UpDownCounter<long>> UpDownCounterCache = new();

    /// <summary>
    /// Gets or creates a Histogram instrument for measuring durations or distributions.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="unit">The unit of measurement (e.g., "ms", "bytes")</param>
    /// <param name="description">Optional description of the metric</param>
    /// <returns>A cached or newly created Histogram instrument</returns>
    public static Histogram<double> GetOrCreateHistogram(string name, string? unit = null, string? description = null)
    {
        return HistogramCache.GetOrAdd(name, _ => 
            Instance.CreateHistogram<double>(name, unit, description));
    }

    /// <summary>
    /// Gets or creates a Counter instrument for counting events (monotonic increase only).
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="unit">The unit of measurement (e.g., "requests", "items")</param>
    /// <param name="description">Optional description of the metric</param>
    /// <returns>A cached or newly created Counter instrument</returns>
    public static Counter<long> GetOrCreateCounter(string name, string? unit = null, string? description = null)
    {
        return CounterCache.GetOrAdd(name, _ => 
            Instance.CreateCounter<long>(name, unit, description));
    }

    /// <summary>
    /// Gets or creates an UpDownCounter instrument for tracking values that can increase and decrease.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="unit">The unit of measurement (e.g., "connections", "items")</param>
    /// <param name="description">Optional description of the metric</param>
    /// <returns>A cached or newly created UpDownCounter instrument</returns>
    public static UpDownCounter<long> GetOrCreateUpDownCounter(string name, string? unit = null, string? description = null)
    {
        return UpDownCounterCache.GetOrAdd(name, _ => 
            Instance.CreateUpDownCounter<long>(name, unit, description));
    }
}

