using System;
using System.Collections.Generic;
using System.Text;
using BBT.Aether.Application.Dtos;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Pagination;

/// <summary>
/// Generates HATEOAS pagination links using the current HTTP request context.
/// Supports reverse proxy scenarios via X-Forwarded-* headers.
/// </summary>
public sealed class PaginationLinkGenerator : IPaginationLinkGenerator
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes the PaginationLinkGenerator with HTTP context accessor.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor for the current HTTP context.</param>
    public PaginationLinkGenerator(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public PaginationLinks GenerateLinks<T>(HateoasPagedList<T> pagedList, string routePath)
    {
        var baseUrl = GetBaseUrl();
        var route = routePath.TrimStart('/');
        var queryParams = GetCurrentQueryParams();

        return new PaginationLinks
        {
            Self = BuildPageLink(baseUrl, route, pagedList.CurrentPage, pagedList.PageSize, queryParams),
            First = BuildPageLink(baseUrl, route, 1, pagedList.PageSize, queryParams),
            Next = pagedList.HasNext
                ? BuildPageLink(baseUrl, route, pagedList.CurrentPage + 1, pagedList.PageSize, queryParams)
                : string.Empty,
            Prev = pagedList.HasPrevious
                ? BuildPageLink(baseUrl, route, pagedList.CurrentPage - 1, pagedList.PageSize, queryParams)
                : string.Empty
        };
    }

    /// <inheritdoc />
    public HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        HateoasPagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath)
    {
        var links = GenerateLinks(pagedList, routePath);
        return new HateoasPagedResultDto<TDto>(items, links);
    }

    /// <inheritdoc />
    public PaginationLinks GenerateLinks<T>(PagedList<T> pagedList, string routePath)
    {
        var baseUrl = GetBaseUrl();
        var route = routePath.TrimStart('/');
        var queryParams = GetCurrentQueryParams();

        return new PaginationLinks
        {
            Self = BuildPageLink(baseUrl, route, pagedList.CurrentPage, pagedList.PageSize, queryParams),
            First = BuildPageLink(baseUrl, route, 1, pagedList.PageSize, queryParams),
            Next = pagedList.HasNext
                ? BuildPageLink(baseUrl, route, pagedList.CurrentPage + 1, pagedList.PageSize, queryParams)
                : string.Empty,
            Prev = pagedList.HasPrevious
                ? BuildPageLink(baseUrl, route, pagedList.CurrentPage - 1, pagedList.PageSize, queryParams)
                : string.Empty
        };
    }

    /// <inheritdoc />
    public HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        PagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath)
    {
        var links = GenerateLinks(pagedList, routePath);
        return new HateoasPagedResultDto<TDto>(items, links);
    }

    /// <summary>
    /// Gets the current request's query parameters.
    /// </summary>
    private IQueryCollection? GetCurrentQueryParams()
    {
        return _httpContextAccessor.HttpContext?.Request.Query;
    }

    /// <summary>
    /// Gets the base URL from the current HTTP request.
    /// Respects X-Forwarded-* headers for reverse proxy scenarios.
    /// </summary>
    private string GetBaseUrl()
    {
        var context = _httpContextAccessor.HttpContext;

        if (context?.Request is null)
        {
            // Fallback for non-HTTP contexts (background jobs, tests)
            return string.Empty;
        }

        var request = context.Request;

        // Support for reverse proxy headers (X-Forwarded-Proto, X-Forwarded-Host)
        var scheme = GetForwardedScheme(request) ?? request.Scheme;
        var host = GetForwardedHost(request) ?? request.Host.ToString();
        var pathBase = request.PathBase.ToString().TrimEnd('/');

        return $"{scheme}://{host}{pathBase}";
    }

    /// <summary>
    /// Gets the forwarded scheme from X-Forwarded-Proto header.
    /// </summary>
    private static string? GetForwardedScheme(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) &&
            !string.IsNullOrEmpty(proto))
        {
            return proto.ToString().Split(',')[0].Trim();
        }

        return null;
    }

    /// <summary>
    /// Gets the forwarded host from X-Forwarded-Host header.
    /// </summary>
    private static string? GetForwardedHost(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-Host", out var host) &&
            !string.IsNullOrEmpty(host))
        {
            return host.ToString().Split(',')[0].Trim();
        }

        return null;
    }

    /// <summary>
    /// Builds a complete page link with all query parameters.
    /// </summary>
    private static string BuildPageLink(
        string baseUrl,
        string route,
        int page,
        int pageSize,
        IQueryCollection? queryParams)
    {
        var sb = new StringBuilder();
        sb.Append(baseUrl);

        if (!string.IsNullOrEmpty(route))
        {
            sb.Append('/');
            sb.Append(route);
        }

        sb.Append('?');
        sb.Append("page=");
        sb.Append(page);
        sb.Append("&pageSize=");
        sb.Append(pageSize);

        if (queryParams is not null)
        {
            foreach (var param in queryParams)
            {
                // Skip pagination params - we're setting them ourselves
                if (param.Key.Equals("page", StringComparison.OrdinalIgnoreCase) ||
                    param.Key.Equals("pageSize", StringComparison.OrdinalIgnoreCase) ||
                    param.Key.Equals("skipCount", StringComparison.OrdinalIgnoreCase) ||
                    param.Key.Equals("maxResultCount", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in param.Value)
                {
                    sb.Append('&');
                    sb.Append(Uri.EscapeDataString(param.Key));
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(value ?? string.Empty));
                }
            }
        }

        return sb.ToString();
    }
}
