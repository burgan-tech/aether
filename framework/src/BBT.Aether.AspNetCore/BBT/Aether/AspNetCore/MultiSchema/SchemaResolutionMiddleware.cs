using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.MultiSchema;

/// <summary>
/// Middleware that resolves the current schema at the beginning of each request
/// and sets it in the <see cref="ICurrentSchema"/> accessor.
/// </summary>
public sealed class SchemaResolutionMiddleware : IMiddleware
{
    private readonly ICurrentSchemaResolver _resolver;
    private readonly ICurrentSchema _currentSchema;
    private readonly IOptions<SchemaResolutionOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaResolutionMiddleware"/> class.
    /// </summary>
    /// <param name="resolver">The schema resolver.</param>
    /// <param name="currentSchema">The current schema instance.</param>
    /// <param name="options">The schema resolution options.</param>
    public SchemaResolutionMiddleware(
        ICurrentSchemaResolver resolver,
        ICurrentSchema currentSchema,
        IOptions<SchemaResolutionOptions> options)
    {
        _resolver = resolver;
        _currentSchema = currentSchema;
        _options = options;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var schema = _resolver.Resolve(context);

        if (schema is null)
        {
            if (_options.Value.ThrowIfNotFound)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Schema could not be resolved.");
                return;
            }
            // Schema not found but not required, continue without setting
        }
        else
        {
            _currentSchema.Set(schema);
        }

        await next(context);
    }
}

