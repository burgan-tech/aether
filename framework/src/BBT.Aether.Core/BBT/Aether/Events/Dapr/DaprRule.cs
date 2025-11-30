namespace BBT.Aether.Events;

/// <summary>
/// Represents a Dapr routing rule (V2 format).
/// </summary>
public class DaprRule
{
    /// <summary>
    /// Gets or sets the CEL expression to match this route.
    /// </summary>
    public string Match { get; set; } = default!;

    /// <summary>
    /// Gets or sets the path of the route.
    /// </summary>
    public string Path { get; set; } = default!;
}

