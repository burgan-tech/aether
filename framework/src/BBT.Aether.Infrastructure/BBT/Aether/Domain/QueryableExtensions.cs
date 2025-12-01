using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain;

/// <summary>
/// Extension methods for IQueryable pagination operations.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Asynchronously converts an IQueryable to a paginated PagedList.
    /// Includes TotalCount (performs COUNT(*) query).
    /// </summary>
    /// <typeparam name="T">The type of elements in the query.</typeparam>
    /// <param name="query">The source query.</param>
    /// <param name="pageNumber">The 1-based page number.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A PagedList containing the paginated results with total count.</returns>
    public async static Task<PagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> query,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        if (pageSize < 1)
        {
            pageSize = 10;
        }

        var count = await query.LongCountAsync(cancellationToken);

        var items = await query
            .PageBy((pageNumber - 1) * pageSize, pageSize)
            .ToListAsync(cancellationToken);

        return new PagedList<T>(items, count, pageNumber, pageSize);
    }

    /// <summary>
    /// Asynchronously converts an IQueryable to a paginated PagedList using PaginationParameters.
    /// Includes TotalCount (performs COUNT(*) query).
    /// </summary>
    /// <typeparam name="T">The type of elements in the query.</typeparam>
    /// <param name="query">The source query.</param>
    /// <param name="parameters">The pagination parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A PagedList containing the paginated results with total count.</returns>
    public async static Task<PagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> query,
        PaginationParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var pageSize = parameters.MaxResultCount;
        var pageNumber = (parameters.SkipCount / pageSize) + 1;

        return await query.ToPagedListAsync(pageNumber, pageSize, cancellationToken);
    }

    /// <summary>
    /// Asynchronously converts an IQueryable to a HATEOAS-optimized HateoasPagedList.
    /// Uses N+1 strategy to determine HasNext without COUNT(*) query for better performance.
    /// </summary>
    /// <typeparam name="T">The type of elements in the query.</typeparam>
    /// <param name="query">The source query.</param>
    /// <param name="pageNumber">The 1-based page number.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A HateoasPagedList containing the paginated results (no TotalCount for performance).</returns>
    public async static Task<HateoasPagedList<T>> ToHateoasPagedListAsync<T>(
        this IQueryable<T> query,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        if (pageSize < 1)
        {
            pageSize = 10;
        }

        var skip = (pageNumber - 1) * pageSize;

        // Fetch pageSize + 1 items to determine if there's a next page
        var items = await query
            .PageBy(skip, pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasNext = items.Count > pageSize;

        // Remove the extra item if exists
        if (hasNext)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new HateoasPagedList<T>(items, pageNumber, pageSize, hasNext);
    }

    /// <summary>
    /// Asynchronously converts an IQueryable to a HATEOAS-optimized HateoasPagedList using PaginationParameters.
    /// Uses N+1 strategy to determine HasNext without COUNT(*) query for better performance.
    /// </summary>
    /// <typeparam name="T">The type of elements in the query.</typeparam>
    /// <param name="query">The source query.</param>
    /// <param name="parameters">The pagination parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A HateoasPagedList containing the paginated results (no TotalCount for performance).</returns>
    public async static Task<HateoasPagedList<T>> ToHateoasPagedListAsync<T>(
        this IQueryable<T> query,
        PaginationParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var pageSize = parameters.MaxResultCount;
        var pageNumber = (parameters.SkipCount / pageSize) + 1;

        return await query.ToHateoasPagedListAsync(pageNumber, pageSize, cancellationToken);
    }
}