using System;
using System.Collections.Generic;

namespace BBT.Aether.Application.Dtos;

/// <summary>
/// Paged result DTO with HATEOAS navigation links.
/// Optimized for performance - TotalCount is optional (null when using N+1 strategy).
/// </summary>
[Serializable]
public class HateoasPagedResultDto<T> : ListResultDto<T>
{
    /// <summary>
    /// HATEOAS navigation links for pagination.
    /// </summary>
    public PaginationLinks Links { get; set; } = new();

    /// <summary>
    /// Creates a new <see cref="HateoasPagedResultDto{T}"/> object.
    /// </summary>
    public HateoasPagedResultDto()
    {
    }

    /// <summary>
    /// Creates a new <see cref="HateoasPagedResultDto{T}"/> object without TotalCount (HATEOAS optimized).
    /// </summary>
    /// <param name="items">List of items in current page</param>
    /// <param name="links">HATEOAS pagination links</param>
    public HateoasPagedResultDto(IReadOnlyList<T> items, PaginationLinks links)
    {
        Items = items;
        Links = links;
    }
}
