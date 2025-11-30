using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Resolves schema from query string parameters.
/// </summary>
public sealed class QueryStringSchemaResolutionStrategy : ISchemaResolutionStrategy
{
    private readonly IOptions<SchemaResolutionOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryStringSchemaResolutionStrategy"/> class.
    /// </summary>
    /// <param name="options">The schema resolution options.</param>
    public QueryStringSchemaResolutionStrategy(IOptions<SchemaResolutionOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public string? TryResolve(HttpContext httpContext)
    {
        var key = _options.Value.QueryStringKey;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (!httpContext.Request.Query.TryGetValue(key, out var values))
            return null;

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

