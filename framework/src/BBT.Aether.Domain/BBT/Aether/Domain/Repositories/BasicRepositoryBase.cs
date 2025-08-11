using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Domain.Repositories;

public abstract class BasicRepositoryBase<TEntity>(IServiceProvider serviceProvider) :
    IBasicRepository<TEntity>,
    IServiceProviderAccessor
    where TEntity : class, IEntity
{
    public bool? IsChangeTrackingEnabled { get; protected set; }
    public IServiceProvider ServiceProvider { get; } = serviceProvider;
    public ILazyServiceProvider LazyServiceProvider => ServiceProvider.GetRequiredService<ILazyServiceProvider>();
    
    public abstract Task<TEntity> InsertAsync(TEntity entity, bool saveChanges = true, CancellationToken cancellationToken = default);

    public abstract Task<TEntity> UpdateAsync(TEntity entity, bool saveChanges = true, CancellationToken cancellationToken = default);
    public abstract Task DeleteAsync(TEntity entity, bool saveChanges = true, CancellationToken cancellationToken = default);

    public abstract Task SaveChangesAsync(CancellationToken cancellationToken = default);

    public abstract Task<List<TEntity>> GetListAsync( bool includeDetails = true, CancellationToken cancellationToken = default);

    public abstract Task<List<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);

    public abstract Task<long> GetCountAsync(CancellationToken cancellationToken = default);

    public abstract Task<PagedList<TEntity>> GetPagedListAsync(PaginationParameters parameters,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);
    
    protected virtual bool ShouldTrackingEntityChange()
    {
        // If IsChangeTrackingEnabled is set, it has the highest priority. This generally means the repository is read-only.
        if (IsChangeTrackingEnabled.HasValue)
        {
            return IsChangeTrackingEnabled.Value;
        }

        // Default behavior is tracking entity change.
        return true;
    }
}

public abstract class BasicRepositoryBase<TEntity, TKey>(IServiceProvider serviceProvider)
    : BasicRepositoryBase<TEntity>(serviceProvider), IBasicRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    public virtual async Task<TEntity> GetAsync(TKey id,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, true, cancellationToken);

        if (entity == null)
        {
            throw new EntityNotFoundException(typeof(TEntity), id);
        }

        return entity;
    }

    public abstract Task<TEntity?> FindAsync(TKey id,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);

    public virtual async Task DeleteAsync(TKey id, bool saveChanges = true, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, cancellationToken: cancellationToken);
        if (entity == null)
        {
            return;
        }

        await DeleteAsync(entity, saveChanges, cancellationToken);
    }
}