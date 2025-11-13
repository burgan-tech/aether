using System.Collections.Generic;

namespace BBT.Aether.Events;

/// <summary>
/// Represents Dapr subscription routes structure (V2 format).
/// </summary>
public class DaprRoutes
{
    /// <summary>
    /// Gets or sets the default route path.
    /// </summary>
    public string? Default { get; set; }

    /// <summary>
    /// Gets or sets the routing rules.
    /// </summary>
    public List<DaprRule>? Rules { get; set; }
}

