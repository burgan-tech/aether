using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Composite resolver that iterates through all registered strategies
/// and returns the first successfully resolved schema.
/// </summary>
public sealed class CompositeSchemaResolver : ICurrentSchemaResolver
{
    private readonly IReadOnlyList<ISchemaResolutionStrategy> _strategies;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeSchemaResolver"/> class.
    /// </summary>
    /// <param name="strategies">The collection of schema resolution strategies.
    /// The order of registration determines the priority.</param>
    public CompositeSchemaResolver(IEnumerable<ISchemaResolutionStrategy> strategies)
    {
        // DI registration order determines priority
        _strategies = strategies.ToList();
    }

    /// <inheritdoc />
    public string? Resolve(HttpContext httpContext)
    {
        foreach (var strategy in _strategies)
        {
            var schema = strategy.TryResolve(httpContext);
            if (!string.IsNullOrWhiteSpace(schema))
                return schema;
        }

        return null;
    }
}

