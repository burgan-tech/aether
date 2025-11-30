namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Configuration options for schema resolution.
/// </summary>
public sealed class SchemaResolutionOptions
{
    /// <summary>
    /// Gets or sets the header key to read schema from (e.g., X-Schema).
    /// </summary>
    public string HeaderKey { get; set; } = "X-Schema";

    /// <summary>
    /// Gets or sets the query string key to read schema from (e.g., schema).
    /// </summary>
    public string QueryStringKey { get; set; } = "schema";

    /// <summary>
    /// Gets or sets the route value key to read schema from (e.g., schema).
    /// </summary>
    public string RouteValueKey { get; set; } = "schema";

    /// <summary>
    /// Gets or sets a value indicating whether to throw a 400 Bad Request
    /// if no resolver can find the schema.
    /// </summary>
    public bool ThrowIfNotFound { get; set; } = true;
}

