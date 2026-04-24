using System.Collections.Generic;

namespace BBT.Aether.Application.Pagination;

/// <summary>
/// Default <see cref="IPaginationContext"/> for hosts that have no transport context
/// (workers, CLI, tests). Reports <see cref="IsAvailable"/> as <c>false</c> so the link
/// generator falls back to route-only links unless an explicit base URL is supplied.
/// </summary>
public sealed class NullPaginationContext : IPaginationContext
{
    public static readonly NullPaginationContext Instance = new();

    public bool IsAvailable => false;

    public string BaseUrl => string.Empty;

    public IReadOnlyList<KeyValuePair<string, string?>> QueryParameters { get; }
        = System.Array.Empty<KeyValuePair<string, string?>>();
}
