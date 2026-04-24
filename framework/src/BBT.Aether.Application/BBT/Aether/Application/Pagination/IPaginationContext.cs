using System.Collections.Generic;

namespace BBT.Aether.Application.Pagination;

/// <summary>
/// Transport-agnostic context that supplies the data needed to build pagination links.
/// Implementations adapt this contract to their host (HTTP request, worker config, tests, ...).
/// The Application layer depends only on this abstraction; concrete adapters live in transport
/// layers (e.g. <c>BBT.Aether.AspNetCore</c>).
/// </summary>
public interface IPaginationContext
{
    /// <summary>
    /// Whether this context can supply meaningful values. When <c>false</c>, callers should
    /// either provide an explicit <c>baseUrl</c> or accept route-only (relative) links.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Base URL (scheme + host + optional path base) for absolute links, e.g. "https://api.example.com".
    /// Empty string means no base URL is available; callers may then produce route-only links.
    /// </summary>
    string BaseUrl { get; }

    /// <summary>
    /// Query parameters that should be preserved on generated links (filters, sorts, ...).
    /// Pagination-control keys (page, pageSize, skipCount, maxResultCount) are filtered out
    /// by the link generator regardless of what is returned here.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string?>> QueryParameters { get; }
}
