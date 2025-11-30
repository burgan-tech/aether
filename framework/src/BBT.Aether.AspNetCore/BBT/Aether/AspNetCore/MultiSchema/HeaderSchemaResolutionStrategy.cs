using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Resolves schema from HTTP request headers.
/// </summary>
public sealed class HeaderSchemaResolutionStrategy : ISchemaResolutionStrategy
{
    private readonly IOptions<SchemaResolutionOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderSchemaResolutionStrategy"/> class.
    /// </summary>
    /// <param name="options">The schema resolution options.</param>
    public HeaderSchemaResolutionStrategy(IOptions<SchemaResolutionOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public string? TryResolve(HttpContext httpContext)
    {
        var headerKey = _options.Value.HeaderKey;
        if (string.IsNullOrWhiteSpace(headerKey))
            return null;

        if (!httpContext.Request.Headers.TryGetValue(headerKey, out var values))
            return null;

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

