using System;
using System.Collections.Generic;
using System.Data;
using BBT.Aether.Uow;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Middleware;

/// <summary>
/// Configuration options for UnitOfWorkMiddleware.
/// Allows customization of Unit of Work behavior per HTTP method and request path.
/// </summary>
public sealed class UnitOfWorkMiddlewareOptions
{
    /// <summary>
    /// Default Unit of Work options used by the middleware.
    /// Default: Reserve pattern (IsTransactional = false) to allow lazy escalation.
    /// Services/aspects can escalate to transactional as needed.
    /// </summary>
    public UnitOfWorkOptions DefaultOptions { get; set; } = new()
    {
        IsTransactional = false, // Reserve pattern
        Scope = UnitOfWorkScopeOption.Required,
        IsolationLevel = null
    };

    /// <summary>
    /// HTTP methods to exclude from UoW (e.g., "GET", "HEAD", "OPTIONS").
    /// Default: empty list (all methods get UoW).
    /// </summary>
    public List<string> ExcludedMethods { get; set; } = new();

    /// <summary>
    /// Path prefixes to exclude from UoW (e.g., /health, /metrics, /_framework).
    /// Supports wildcards: /health*, /api/*/websocket
    /// Useful for health checks, static resources, and WebSocket connections.
    /// </summary>
    public List<string> ExcludedPathPrefixes { get; set; } = new()
    {
        "/",
        "/health",
        "/healthz",
        "/metrics",
        "/_framework/*",
        "/swagger/*"
    };

    /// <summary>
    /// Custom predicate to exclude specific requests from Unit of Work management.
    /// Return true to exclude the request (skip UoW), false to process normally.
    /// Useful for dynamic exclusions like GraphQL introspection, Hangfire dashboard, etc.
    /// Example: opt.ExcludeWhen = ctx => ctx.Request.Path.StartsWithSegments("/hangfire");
    /// </summary>
    public Func<HttpContext, bool>? ExcludeWhen { get; set; }

    /// <summary>
    /// HTTP method-specific Unit of Work behaviors (backward compatibility).
    /// Key: HTTP method (case-insensitive), Value: UoW behavior configuration.
    /// Note: This is kept for backward compatibility but is superseded by DefaultOptions.
    /// </summary>
    [Obsolete("Use DefaultOptions instead. This property is kept for backward compatibility.")]
    public Dictionary<string, HttpMethodUnitOfWorkBehavior> HttpMethodBehaviors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default Unit of Work behavior for HTTP methods (backward compatibility).
    /// Note: This is kept for backward compatibility but is superseded by DefaultOptions.
    /// </summary>
    [Obsolete("Use DefaultOptions instead. This property is kept for backward compatibility.")]
    public HttpMethodUnitOfWorkBehavior? DefaultBehavior { get; set; }
}

