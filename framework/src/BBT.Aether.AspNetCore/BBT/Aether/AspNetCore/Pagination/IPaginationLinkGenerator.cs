using System.Collections.Generic;
using BBT.Aether.Application.Dtos;

namespace BBT.Aether.AspNetCore.Pagination;

/// <summary>
/// Generates HATEOAS pagination links for API responses.
/// Should be used in the API/Controller layer only.
/// </summary>
public interface IPaginationLinkGenerator
{
    /// <summary>
    /// Generates pagination links based on the current request context.
    /// Uses HateoasPagedList which is optimized for performance (no TotalCount).
    /// </summary>
    /// <typeparam name="T">The type of paginated items.</typeparam>
    /// <param name="pagedList">The paginated data from repository (HATEOAS optimized).</param>
    /// <param name="routePath">The route path (e.g., "instances", "users").</param>
    /// <returns>HATEOAS pagination links.</returns>
    PaginationLinks GenerateLinks<T>(HateoasPagedList<T> pagedList, string routePath);

    /// <summary>
    /// Creates a complete HATEOAS paged result by combining mapped items with generated links.
    /// Uses HateoasPagedList which is optimized for performance (no TotalCount).
    /// </summary>
    /// <typeparam name="TEntity">The entity type from the repository.</typeparam>
    /// <typeparam name="TDto">The DTO type for the response.</typeparam>
    /// <param name="pagedList">The paginated data from repository (HATEOAS optimized).</param>
    /// <param name="items">The mapped DTO items.</param>
    /// <param name="routePath">The route path (e.g., "instances", "users").</param>
    /// <returns>HATEOAS paged result with items and navigation links.</returns>
    HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        HateoasPagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath);

    /// <summary>
    /// Generates pagination links for standard PagedList (includes TotalCount).
    /// Use this when you need total count information.
    /// </summary>
    /// <typeparam name="T">The type of paginated items.</typeparam>
    /// <param name="pagedList">The paginated data from repository.</param>
    /// <param name="routePath">The route path (e.g., "instances", "users").</param>
    /// <returns>HATEOAS pagination links.</returns>
    PaginationLinks GenerateLinks<T>(PagedList<T> pagedList, string routePath);

    /// <summary>
    /// Creates a complete HATEOAS paged result using standard PagedList.
    /// Use this when you need total count information.
    /// </summary>
    /// <typeparam name="TEntity">The entity type from the repository.</typeparam>
    /// <typeparam name="TDto">The DTO type for the response.</typeparam>
    /// <param name="pagedList">The paginated data from repository.</param>
    /// <param name="items">The mapped DTO items.</param>
    /// <param name="routePath">The route path (e.g., "instances", "users").</param>
    /// <returns>HATEOAS paged result with items, navigation links, and total count.</returns>
    HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        PagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath);
}

