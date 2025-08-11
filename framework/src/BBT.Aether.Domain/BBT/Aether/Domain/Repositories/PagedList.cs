using System;
using System.Collections.Generic;

namespace BBT.Aether.Domain.Repositories;

//TODO: Href yap??s?? ve alternatifleri yap??land??r??lacak.
/// <summary>
/// Represents a paged list of items.
/// </summary>
/// <typeparam name="T">The type of the items in the list.</typeparam>
/// <param name="items">The list of items.</param>
/// <param name="count">The total number of items.</param>
/// <param name="pageNumber">The current page number.</param>
/// <param name="pageSize">The size of each page.</param>
public class PagedList<T>(IList<T> items, long count, int pageNumber, int pageSize)
{
    /// <summary>
    /// Gets the current page number.
    /// </summary>
    public int CurrentPage { get; private set; } = pageNumber;
    /// <summary>
    /// Gets the total number of pages.
    /// </summary>
    public int TotalPages { get; private set; } = (int)Math.Ceiling(count / (double)pageSize);
    /// <summary>
    /// Gets the size of each page.
    /// </summary>
    public int PageSize { get; private set; } = pageSize;
    /// <summary>
    /// Gets the total number of items.
    /// </summary>
    public long TotalCount { get; private set; } = count;
    /// <summary>
    /// Gets a value indicating whether there is a previous page.
    /// </summary>
    public bool HasPrevious => CurrentPage > 1;
    /// <summary>
    /// Gets a value indicating whether there is a next page.
    /// </summary>
    public bool HasNext => CurrentPage < TotalPages;
    /// <summary>
    /// Gets the list of items for the current page.
    /// </summary>
    public IList<T> Items { get; private set; } = items;
}