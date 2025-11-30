using System.Data;

namespace BBT.Aether.AspNetCore.Middleware;

/// <summary>
/// Defines Unit of Work behavior for a specific HTTP method.
/// Allows granular control over transaction settings per HTTP verb.
/// </summary>
public sealed class HttpMethodUnitOfWorkBehavior
{
    /// <summary>
    /// The HTTP method this behavior applies to (e.g., "GET", "POST", "PUT").
    /// Use "*" for default/fallback behavior.
    /// </summary>
    public string HttpMethod { get; set; } = default!;

    /// <summary>
    /// Whether operations should be wrapped in a database transaction.
    /// Default: true for write operations (POST, PUT, DELETE, PATCH), false for read operations (GET, HEAD, OPTIONS).
    /// </summary>
    public bool IsTransactional { get; set; } = true;

    /// <summary>
    /// Transaction isolation level.
    /// Default: ReadCommitted
    /// </summary>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.ReadCommitted;
}

