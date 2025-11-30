using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Resolves schema from route values.
/// Note: This requires the middleware to be placed after UseRouting() in the pipeline.
/// </summary>
public sealed class RouteSchemaResolutionStrategy : ISchemaResolutionStrategy
{
    private readonly IOptions<SchemaResolutionOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouteSchemaResolutionStrategy"/> class.
    /// </summary>
    /// <param name="options">The schema resolution options.</param>
    public RouteSchemaResolutionStrategy(IOptions<SchemaResolutionOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public string? TryResolve(HttpContext httpContext)
    {
        var key = _options.Value.RouteValueKey;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (httpContext.Request.RouteValues.TryGetValue(key, out var valueObj) &&
            valueObj is string value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }
}

