namespace BBT.Aether.Aspects;

/// <summary>
/// Defines the metric type for the Metric aspect.
/// </summary>
public enum MetricType
{
    /// <summary>
    /// Records duration distribution using a Histogram instrument.
    /// Best for measuring method execution time, latency, or size distributions.
    /// This is the default metric type.
    /// </summary>
    Histogram,

    /// <summary>
    /// Counts method invocations using a Counter instrument (monotonic increase only).
    /// Best for counting events like method calls, processed items, or errors.
    /// </summary>
    Counter,

    /// <summary>
    /// Tracks in-progress method executions using an UpDownCounter instrument (can increase and decrease).
    /// Best for tracking concurrent operations, queue depth, or active connections.
    /// </summary>
    UpDownCounter
}

