using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.Middleware;

/// <summary>
/// Middleware that automatically manages Unit of Work for HTTP requests.
/// Starts UoW before request processing, commits on success, rolls back on exception.
/// Configurable via UnitOfWorkMiddlewareOptions to exclude specific paths/methods.
/// </summary>
public sealed class UnitOfWorkMiddleware : IMiddleware
{
    private readonly IUnitOfWorkManager _uowManager;
    private readonly UnitOfWorkMiddlewareOptions _options;

    /// <summary>
    /// Initializes a new instance of UnitOfWorkMiddleware.
    /// </summary>
    /// <param name="uowManager">The unit of work manager</param>
    /// <param name="options">Configuration options</param>
    public UnitOfWorkMiddleware(
        IUnitOfWorkManager uowManager,
        IOptions<UnitOfWorkMiddlewareOptions> options)
    {
        _uowManager = uowManager;
        _options = options.Value;
    }

    /// <summary>
    /// Invokes the middleware to manage Unit of Work for the request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Check if UoW should be started for this request
        if (!ShouldStartUnitOfWork(context))
        {
            await next(context);
            return;
        }

        // Start UoW with configured default options (reserve pattern)
        await using var scope = await _uowManager.BeginAsync(_options.DefaultOptions, context.RequestAborted);

        // Process request
        await next(context);

        // Auto-commit on success (exception triggers auto-rollback via DisposeAsync)
        await scope.CommitAsync(context.RequestAborted);
    }

    /// <summary>
    /// Determines whether Unit of Work should be started for the given request.
    /// Excludes WebSocket requests, custom exclusions, excluded methods, and excluded paths.
    /// </summary>
    private bool ShouldStartUnitOfWork(HttpContext context)
    {
        // Check WebSocket requests first - exclude them
        if (context.WebSockets.IsWebSocketRequest)
        {
            return false;
        }

        // Check custom exclusion predicate
        if (_options.ExcludeWhen?.Invoke(context) ?? false)
        {
            return false;
        }

        // Check excluded HTTP methods
        foreach (var excludedMethod in _options.ExcludedMethods)
        {
            if (string.Equals(excludedMethod, context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Check excluded path prefixes
        var path = context.Request.Path.Value ?? "/";
        foreach (var excludedPath in _options.ExcludedPathPrefixes)
        {
            if (IsPathMatch(path, excludedPath))
            {
                return false;
            }
        }

        // Start UoW for all other requests
        return true;
    }

    /// <summary>
    /// Checks if a path matches a pattern (supports wildcards).
    /// </summary>
    private static bool IsPathMatch(string path, string pattern)
    {
        // Simple wildcard matching for patterns ending with *
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // More complex pattern matching for patterns with * in the middle
        if (pattern.Contains("*"))
        {
            // Convert pattern to regex (e.g., /api/*/websocket -> ^/api/.*/websocket$)
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase);
        }

        // Exact match
        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

