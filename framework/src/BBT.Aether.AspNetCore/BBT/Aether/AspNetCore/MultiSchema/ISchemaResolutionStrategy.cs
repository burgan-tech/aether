using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Strategy for resolving schema from various sources (route, header, query string, etc.).
/// </summary>
public interface ISchemaResolutionStrategy
{
    /// <summary>
    /// Attempts to resolve the schema from the HTTP context.
    /// </summary>
    /// <param name="httpContext">The HTTP context.</param>
    /// <returns>The schema name if resolved; otherwise, null. Should not throw exceptions.</returns>
    string? TryResolve(HttpContext httpContext);
}

