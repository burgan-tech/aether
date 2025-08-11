using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Application.Services;

public abstract class AbstractKeyEntityReadOnlyAppService<TEntity, TKey>(
    IServiceProvider serviceProvider,
    IReadOnlyRepository<TEntity> repository)
    : ApplicationService(serviceProvider), IReadOnlyEntityAppService<TEntity, TKey>
    where TEntity : class, IEntity
{
    protected IReadOnlyRepository<TEntity> ReadOnlyRepository { get; } = repository;
    
    public virtual async Task<TEntity> GetAsync(TKey id)
    {
        return await GetEntityByIdAsync(id);
    }

    public virtual async Task<PagedList<TEntity>> GetPagedListAsync(PaginationParameters input)
    {
        return await ReadOnlyRepository.GetPagedListAsync(input);
    }

    public virtual async Task<IList<TEntity>> GetListAsync()
    {
        var query = await CreateFilteredQueryAsync();
        query = ApplyDefaultSorting(query);
        return await query.ToListAsync();
    }

    /// <summary>
    /// This method should create <see cref="IQueryable{TEntity}"/> based on given input.
    /// It should filter query if needed, but should not do sorting or paging.
    /// methods.
    /// </summary>
    protected virtual async Task<IQueryable<TEntity>> CreateFilteredQueryAsync()
    {
        return await ReadOnlyRepository.GetQueryableAsync();
    }

    protected abstract Task<TEntity> GetEntityByIdAsync(TKey id);

    /// <summary>
    /// Applies sorting if no sorting specified but a limited result requested.
    /// </summary>
    /// <param name="query">The query.</param>
    protected virtual IQueryable<TEntity> ApplyDefaultSorting(IQueryable<TEntity> query)
    {
        if (typeof(TEntity).IsAssignableTo<IHasCreatedAt>())
        {
            return query.OrderByDescending(e => ((IHasCreatedAt)e).CreatedAt);
        }

        return query;
    }
}