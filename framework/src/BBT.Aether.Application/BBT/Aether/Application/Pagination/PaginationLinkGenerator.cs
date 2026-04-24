using System;
using System.Collections.Generic;
using System.Text;
using BBT.Aether.Application.Dtos;

namespace BBT.Aether.Application.Pagination;

/// <summary>
/// Transport-agnostic implementation of <see cref="IPaginationLinkGenerator"/>.
/// Resolves the base URL through <see cref="IPaginationContext"/>; callers can opt out of
/// context resolution via <see cref="Relative"/> or <see cref="WithBaseUrl"/>.
/// </summary>
public sealed class PaginationLinkGenerator : IPaginationLinkGenerator
{
    private static readonly HashSet<string> PaginationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "page",
        "pageSize",
        "skipCount",
        "maxResultCount"
    };

    private readonly IPaginationContext _context;

    public PaginationLinkGenerator(IPaginationContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public IPaginationLinkGenerator Relative()
        => new FixedBaseUrlPaginationLinkGenerator(_context, baseUrl: string.Empty);

    /// <inheritdoc />
    public IPaginationLinkGenerator WithBaseUrl(string baseUrl)
    {
        if (baseUrl is null)
        {
            throw new ArgumentNullException(nameof(baseUrl));
        }
        return new FixedBaseUrlPaginationLinkGenerator(_context, baseUrl);
    }

    /// <inheritdoc />
    public PaginationLinks GenerateLinks<T>(HateoasPagedList<T> pagedList, string routePath)
        => BuildLinks(ResolveContextBaseUrl(), pagedList.CurrentPage, pagedList.PageSize,
            pagedList.HasNext, pagedList.HasPrevious, routePath, _context.QueryParameters);

    /// <inheritdoc />
    public PaginationLinks GenerateLinks<T>(PagedList<T> pagedList, string routePath)
        => BuildLinks(ResolveContextBaseUrl(), pagedList.CurrentPage, pagedList.PageSize,
            pagedList.HasNext, pagedList.HasPrevious, routePath, _context.QueryParameters);

    /// <inheritdoc />
    public HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        HateoasPagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath)
        => new(items, GenerateLinks(pagedList, routePath));

    /// <inheritdoc />
    public HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        PagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath)
        => new(items, GenerateLinks(pagedList, routePath));

    private string ResolveContextBaseUrl()
        => _context.IsAvailable ? _context.BaseUrl : string.Empty;

    /// <summary>
    /// Core link-building algorithm. Shared by the default generator and the fixed-base-url
    /// decorator so the URL composition logic lives in exactly one place.
    /// </summary>
    internal static PaginationLinks BuildLinks(
        string baseUrl,
        int currentPage,
        int pageSize,
        bool hasNext,
        bool hasPrevious,
        string routePath,
        IReadOnlyList<KeyValuePair<string, string?>> queryParams)
    {
        var route = routePath.TrimStart('/');

        return new PaginationLinks
        {
            Self = BuildPageLink(baseUrl, route, currentPage, pageSize, queryParams),
            First = BuildPageLink(baseUrl, route, 1, pageSize, queryParams),
            Next = hasNext
                ? BuildPageLink(baseUrl, route, currentPage + 1, pageSize, queryParams)
                : string.Empty,
            Prev = hasPrevious
                ? BuildPageLink(baseUrl, route, currentPage - 1, pageSize, queryParams)
                : string.Empty
        };
    }

    private static string BuildPageLink(
        string baseUrl,
        string route,
        int page,
        int pageSize,
        IReadOnlyList<KeyValuePair<string, string?>> queryParams)
    {
        var sb = new StringBuilder();
        // Trim a trailing '/' from baseUrl so we never produce '//' between base and route
        // (e.g. when callers pass "https://x.com/" via WithBaseUrl).
        sb.Append(baseUrl.AsSpan().TrimEnd('/'));

        if (!string.IsNullOrEmpty(route))
        {
            // Always separate base URL from route with '/'. When baseUrl is empty,
            // this produces a root-relative link (e.g. "/users?...") which browsers
            // and HTTP clients resolve consistently regardless of current path.
            sb.Append('/');
            sb.Append(route);
        }

        sb.Append('?');
        sb.Append("page=");
        sb.Append(page);
        sb.Append("&pageSize=");
        sb.Append(pageSize);

        for (var i = 0; i < queryParams.Count; i++)
        {
            var param = queryParams[i];
            if (PaginationKeys.Contains(param.Key))
            {
                continue;
            }

            sb.Append('&');
            sb.Append(Uri.EscapeDataString(param.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(param.Value ?? string.Empty));
        }

        return sb.ToString();
    }
}
