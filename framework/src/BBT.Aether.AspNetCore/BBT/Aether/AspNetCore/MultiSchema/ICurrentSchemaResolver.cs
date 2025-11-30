using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Resolves the current schema by iterating through all registered strategies.
/// </summary>
public interface ICurrentSchemaResolver
{
    /// <summary>
    /// Resolves the schema from the HTTP context using registered strategies.
    /// </summary>
    /// <param name="httpContext">The HTTP context.</param>
    /// <returns>The resolved schema name, or null if not found.</returns>
    string? Resolve(HttpContext httpContext);
}

