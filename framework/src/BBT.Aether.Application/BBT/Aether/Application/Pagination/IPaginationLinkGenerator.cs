using System.Collections.Generic;
using BBT.Aether.Application.Dtos;

namespace BBT.Aether.Application.Pagination;

/// <summary>
/// Generates HATEOAS pagination links for paged responses.
/// The base URL is resolved from the ambient transport context (e.g. HTTP request).
/// To override that behavior, use <see cref="Relative"/> for route-only links or
/// <see cref="WithBaseUrl"/> for an explicit absolute base URL.
/// </summary>
public interface IPaginationLinkGenerator
{
    /// <summary>
    /// Returns a generator that ignores the ambient transport context and produces
    /// route-only (relative) links such as <c>"users?page=2&amp;pageSize=10"</c>.
    /// Query parameters from the context are still preserved.
    /// </summary>
    IPaginationLinkGenerator Relative();

    /// <summary>
    /// Returns a generator that uses the supplied <paramref name="baseUrl"/> instead of
    /// the ambient transport context. Pass an absolute URL such as <c>"https://api.example.com"</c>.
    /// </summary>
    /// <param name="baseUrl">Absolute base URL to prefix every link with. Must not be null.</param>
    IPaginationLinkGenerator WithBaseUrl(string baseUrl);

    /// <summary>
    /// Generates HATEOAS pagination links for a <see cref="HateoasPagedList{T}"/>
    /// (optimized: no TotalCount).
    /// </summary>
    PaginationLinks GenerateLinks<T>(HateoasPagedList<T> pagedList, string routePath);

    /// <summary>
    /// Generates HATEOAS pagination links for a standard <see cref="PagedList{T}"/>
    /// (includes TotalCount).
    /// </summary>
    PaginationLinks GenerateLinks<T>(PagedList<T> pagedList, string routePath);

    /// <summary>
    /// Builds a complete <see cref="HateoasPagedResultDto{TDto}"/> from a HATEOAS-optimized
    /// paged list and pre-mapped items.
    /// </summary>
    HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        HateoasPagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath);

    /// <summary>
    /// Builds a complete <see cref="HateoasPagedResultDto{TDto}"/> from a standard paged list
    /// (with TotalCount) and pre-mapped items.
    /// </summary>
    HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        PagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath);
}
