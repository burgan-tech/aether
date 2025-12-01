using System.Collections.Generic;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Represents a paged list optimized for HATEOAS responses.
/// Does not include TotalCount for performance (avoids COUNT(*) query).
/// Uses N+1 strategy to determine HasNext.
/// </summary>
/// <typeparam name="T">The type of the items in the list.</typeparam>
public class HateoasPagedList<T>
{
    /// <summary>
    /// Creates a new HateoasPagedList.
    /// </summary>
    /// <param name="items">The list of items for the current page.</param>
    /// <param name="pageNumber">The current page number (1-based).</param>
    /// <param name="pageSize">The size of each page.</param>
    /// <param name="hasNext">Whether there is a next page.</param>
    public HateoasPagedList(IList<T> items, int pageNumber, int pageSize, bool hasNext)
    {
        Items = items;
        CurrentPage = pageNumber;
        PageSize = pageSize;
        HasNext = hasNext;
    }

    /// <summary>
    /// Gets the current page number (1-based).
    /// </summary>
    public int CurrentPage { get; }

    /// <summary>
    /// Gets the size of each page.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets a value indicating whether there is a previous page.
    /// </summary>
    public bool HasPrevious => CurrentPage > 1;

    /// <summary>
    /// Gets a value indicating whether there is a next page.
    /// Determined by N+1 strategy without COUNT(*) query.
    /// </summary>
    public bool HasNext { get; }

    /// <summary>
    /// Gets the list of items for the current page.
    /// </summary>
    public IList<T> Items { get; }
}

