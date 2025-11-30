namespace BBT.Aether.Aspects;

/// <summary>
/// Defines the tracing mode for the Trace aspect.
/// </summary>
public enum TracingMode
{
    /// <summary>
    /// Creates a new Activity (span) for the method execution.
    /// This is the default mode and creates a child span in the distributed trace.
    /// </summary>
    Span,

    /// <summary>
    /// Adds ActivityEvents to the current Activity without creating a new span.
    /// Useful for marking significant points in execution within an existing span.
    /// </summary>
    Event,

    /// <summary>
    /// Only enriches the current Activity with tags without creating a new span or events.
    /// Useful for adding contextual information to the parent span.
    /// </summary>
    Enrich
}

