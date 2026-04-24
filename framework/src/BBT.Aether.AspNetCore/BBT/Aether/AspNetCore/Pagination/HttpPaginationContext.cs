using System.Collections.Generic;
using BBT.Aether.Application.Pagination;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Pagination;

/// <summary>
/// HTTP-aware <see cref="IPaginationContext"/> backed by <see cref="IHttpContextAccessor"/>.
/// Supports reverse-proxy deployments via <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c>
/// headers, and exposes the current request's query string for link preservation.
/// </summary>
public sealed class HttpPaginationContext : IPaginationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpPaginationContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsAvailable => _httpContextAccessor.HttpContext?.Request is not null;

    public string BaseUrl
    {
        get
        {
            var request = _httpContextAccessor.HttpContext?.Request;
            if (request is null)
            {
                return string.Empty;
            }

            var scheme = GetForwardedScheme(request) ?? request.Scheme;
            var host = GetForwardedHost(request) ?? request.Host.ToString();
            var pathBase = request.PathBase.ToString().TrimEnd('/');

            return $"{scheme}://{host}{pathBase}";
        }
    }

    public IReadOnlyList<KeyValuePair<string, string?>> QueryParameters
    {
        get
        {
            var query = _httpContextAccessor.HttpContext?.Request.Query;
            if (query is null || query.Count == 0)
            {
                return System.Array.Empty<KeyValuePair<string, string?>>();
            }

            var result = new List<KeyValuePair<string, string?>>(query.Count);
            foreach (var entry in query)
            {
                foreach (var value in entry.Value)
                {
                    result.Add(new KeyValuePair<string, string?>(entry.Key, value));
                }
            }
            return result;
        }
    }

    private static string? GetForwardedScheme(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) &&
            !string.IsNullOrEmpty(proto))
        {
            return proto.ToString().Split(',')[0].Trim();
        }

        return null;
    }

    private static string? GetForwardedHost(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-Host", out var host) &&
            !string.IsNullOrEmpty(host))
        {
            return host.ToString().Split(',')[0].Trim();
        }

        return null;
    }
}
