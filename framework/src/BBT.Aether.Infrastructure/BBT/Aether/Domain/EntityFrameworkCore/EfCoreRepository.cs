using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Guids;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Domain.EntityFrameworkCore;

public class EfCoreRepository<TDbContext, TEntity> : RepositoryBase<TEntity>, IEfCoreRepository<TEntity>
    where TDbContext : AetherDbContext<TDbContext>
    where TEntity : class, IEntity
{
    private readonly TDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance with explicit service provider (recommended).
    /// </summary>
    public EfCoreRepository(
        TDbContext dbContext,
        IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Initializes a new instance relying on AmbientServiceProvider.
    /// </summary>
    public EfCoreRepository(TDbContext dbContext)
        : base()
    {
        _dbContext = dbContext;
    }

    public IGuidGenerator GuidGenerator =>
        LazyServiceProvider.LazyGetService<IGuidGenerator>(SimpleGuidGenerator.Instance);

    public ICurrentUser CurrentUser => LazyServiceProvider.LazyGetRequiredService<ICurrentUser>();

    public IClock Clock => LazyServiceProvider.LazyGetRequiredService<IClock>();
    
    protected virtual bool ShouldSaveChanges(bool saveChanges)
    {
        return saveChanges;
    }

    async Task<DbContext> IEfCoreRepository<TEntity>.GetDbContextAsync()
    {
        return await GetDbContextAsync();
    }

    protected virtual Task<TDbContext> GetDbContextAsync()
    {
        return Task.FromResult(_dbContext);
    }

    Task<DbSet<TEntity>> IEfCoreRepository<TEntity>.GetDbSetAsync()
    {
        return GetDbSetAsync();
    }

    protected async Task<DbSet<TEntity>> GetDbSetAsync()
    {
        return (await GetDbContextAsync()).Set<TEntity>();
    }

    protected async Task<IDbConnection> GetDbConnectionAsync()
    {
        return (await GetDbContextAsync()).Database.GetDbConnection();
    }

    protected async Task<IDbTransaction?> GetDbTransactionAsync()
    {
        return (await GetDbContextAsync()).Database.CurrentTransaction?.GetDbTransaction();
    }

    public async override Task<TEntity> InsertAsync(TEntity entity, bool saveChanges = false,
        CancellationToken cancellationToken = default)
    {
        CheckAndSetId(entity);
        var context = await this.GetDbContextAsync();

        var savedEntity = (await context.Set<TEntity>().AddAsync(entity, cancellationToken)).Entity;
        if (ShouldSaveChanges(saveChanges))
        {
            await SaveChangesAsync(cancellationToken);
        }

        return savedEntity;
    }

    public async override Task<TEntity> UpdateAsync(TEntity entity, bool saveChanges = false,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        if (context.Set<TEntity>().Local.All(e => e != entity))
        {
            context.Set<TEntity>().Attach(entity);
            context.Update(entity);
        }

        if (ShouldSaveChanges(saveChanges))
        {
            await SaveChangesAsync(cancellationToken);
        }

        return entity;
    }

    public async override Task DeleteAsync(TEntity entity, bool saveChanges = false,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        context.Set<TEntity>().Remove(entity);
        if (ShouldSaveChanges(saveChanges))
        {
            await SaveChangesAsync(cancellationToken);
        }
    }

    public async override Task<List<TEntity>> GetListAsync(
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync()).ToListAsync(cancellationToken)
            : await (await GetQueryableAsync()).ToListAsync(cancellationToken);
    }

    public async override Task<List<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync()).Where(predicate).ToListAsync(cancellationToken)
            : await (await GetQueryableAsync()).Where(predicate).ToListAsync(cancellationToken);
    }

    public async override Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync()).LongCountAsync(cancellationToken);
    }

    public async override Task<PagedList<TEntity>> GetPagedListAsync(
        PaginationParameters parameters,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = includeDetails
            ? await WithDetailsAsync()
            : await GetQueryableAsync();

        var items = await queryable
            .OrderByIf<TEntity, IQueryable<TEntity>>(!parameters.Sorting.IsNullOrWhiteSpace(), parameters.Sorting!)
            .PageBy(parameters.SkipCount, parameters.MaxResultCount)
            .ToListAsync(cancellationToken);

        var count = await queryable.LongCountAsync(cancellationToken);

        return new PagedList<TEntity>(
            items,
            count,
            parameters.SkipCount,
            parameters.MaxResultCount);
    }

    public async override Task<IQueryable<TEntity>> GetQueryableAsync()
    {
        return (await GetDbSetAsync()).AsQueryable().AsNoTrackingIf(!ShouldTrackingEntityChange());
    }

    public async override Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync())
                .Where(predicate)
                .SingleOrDefaultAsync(cancellationToken)
            : await (await GetQueryableAsync())
                .Where(predicate)
                .SingleOrDefaultAsync(cancellationToken);
    }

    public async override Task DeleteAsync(Expression<Func<TEntity, bool>> predicate,
        bool saveChanges = false,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        var dbSet = context.Set<TEntity>();

        var entities = await dbSet
            .Where(predicate)
            .ToListAsync(cancellationToken);

        foreach (var entity in entities)
        {
            await DeleteAsync(entity, saveChanges, cancellationToken);
        }

        if (ShouldSaveChanges(saveChanges))
        {
            await SaveChangesAsync(cancellationToken);
        }
    }

    public async override Task DeleteDirectAsync(Expression<Func<TEntity, bool>> predicate,
        bool saveChanges = false,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        var dbSet = context.Set<TEntity>();
        await dbSet.Where(predicate).ExecuteDeleteAsync(cancellationToken);
    }

    public virtual async Task EnsureCollectionLoadedAsync<TProperty>(
        TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
    {
        await (await GetDbContextAsync())
            .Entry(entity)
            .Collection(propertyExpression)
            .LoadAsync(cancellationToken);
    }

    public virtual async Task EnsurePropertyLoadedAsync<TProperty>(
        TEntity entity,
        Expression<Func<TEntity, TProperty?>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
    {
        await (await GetDbContextAsync())
            .Entry(entity)
            .Reference(propertyExpression)
            .LoadAsync(cancellationToken);
    }
    
    public async override Task<IQueryable<TEntity>> WithDetailsAsync()
    {
        return await GetQueryableAsync();
    }

    public async override Task<IQueryable<TEntity>> WithDetailsAsync(
        params Expression<Func<TEntity, object>>[] propertySelectors)
    {
        return IncludeDetails(
            await GetQueryableAsync(),
            propertySelectors
        );
    }
    
    private static IQueryable<TEntity> IncludeDetails(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, object>>[] propertySelectors)
    {
        if (!propertySelectors.IsNullOrEmpty())
        {
            foreach (var propertySelector in propertySelectors)
            {
                query = query.Include(propertySelector);
            }
        }

        return query;
    }

    public async override Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        await context.SaveChangesAsync(cancellationToken);
    }

    protected virtual void CheckAndSetId(TEntity entity)
    {
        if (entity is IEntity<Guid> entityWithGuidId)
        {
            TrySetGuidId(entityWithGuidId);
        }
    }

    protected virtual void TrySetGuidId(IEntity<Guid> entity)
    {
        if (entity.Id != default)
        {
            return;
        }

        EntityHelper.TrySetId(
            entity,
            () => GuidGenerator.Create(),
            true
        );
    }
}

public class EfCoreRepository<TDbContext, TEntity, TKey> : EfCoreRepository<TDbContext, TEntity>,
        IEfCoreRepository<TEntity, TKey>
    where TDbContext : AetherDbContext<TDbContext>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Initializes a new instance with explicit service provider (recommended).
    /// </summary>
    public EfCoreRepository(
        TDbContext dbContext,
        IServiceProvider serviceProvider)
        : base(dbContext, serviceProvider)
    {
    }

    /// <summary>
    /// Initializes a new instance relying on AmbientServiceProvider.
    /// </summary>
    public EfCoreRepository(TDbContext dbContext)
        : base(dbContext)
    {
    }
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

    public virtual async Task<TEntity?> FindAsync(TKey id,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync()).OrderBy(e => e.Id)
                .FirstOrDefaultAsync(e => e.Id!.Equals(id), cancellationToken)
            : !ShouldTrackingEntityChange()
                ? await (await GetQueryableAsync()).OrderBy(e => e.Id)
                    .FirstOrDefaultAsync(e => e.Id!.Equals(id), cancellationToken)
                : await (await GetDbSetAsync()).FindAsync(new object[] { id! }, cancellationToken);
    }

    public virtual async Task DeleteAsync(TKey id, bool saveChanges = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, cancellationToken: cancellationToken);
        if (entity == null)
        {
            return;
        }

        await DeleteAsync(entity, saveChanges, cancellationToken);
    }
}