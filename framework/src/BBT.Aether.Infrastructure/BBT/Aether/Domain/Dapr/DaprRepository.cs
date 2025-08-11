using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Dapr.Client;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.Dapr;

public class DaprRepository<TDbContext, TEntity>(
    TDbContext dbContext,
    IServiceProvider serviceProvider,
    string storeName)
    : RepositoryBase<TEntity>(serviceProvider), IDaprRepository<TEntity>
    where TDbContext : DaprClient
    where TEntity : class, IEntity
{
    protected string StoreName { get; } = storeName;
    protected virtual string EntityName => typeof(TEntity).Name;
    protected const string KeyFormat = "{0}.{1}";

    public async override Task<TEntity> InsertAsync(TEntity entity, bool saveChanges = true,
        CancellationToken cancellationToken = default)
    {
        var stateKey = string.Format(KeyFormat, this.EntityName, entity.GetKeys());
        await dbContext.SaveStateAsync(StoreName, stateKey, entity, cancellationToken: cancellationToken);

        // Update keys list
        var keys = await dbContext.GetStateAsync<List<string>>(
            StoreName,
            string.Format(KeyFormat, this.EntityName, "keys"),
            cancellationToken: cancellationToken) ?? new List<string>();
        if (!keys.Contains(stateKey))
        {
            keys.Add(stateKey);
            await dbContext.SaveStateAsync(
                StoreName,
                string.Format(KeyFormat, this.EntityName, "keys"),
                keys,
                cancellationToken: cancellationToken);
        }

        return entity;
    }

    public async override Task<TEntity> UpdateAsync(TEntity entity, bool saveChanges = true,
        CancellationToken cancellationToken = default)
    {
        var stateKey = string.Format(KeyFormat, this.EntityName, entity.GetKeys());
        await dbContext.SaveStateAsync(
            StoreName,
            stateKey,
            entity,
            cancellationToken: cancellationToken);
        return entity;
    }

    public async override Task DeleteAsync(TEntity entity, bool saveChanges = true,
        CancellationToken cancellationToken = default)
    {
        var stateKey = string.Format(KeyFormat, this.EntityName, entity.GetKeys());

        // Update keys list
        var keys = await dbContext.GetStateAsync<List<string>>(
            StoreName,
            string.Format(KeyFormat, this.EntityName, "keys"),
            cancellationToken: cancellationToken) ?? new List<string>();

        if (keys.Contains(stateKey))
        {
            keys.Remove(stateKey);
            await dbContext.SaveStateAsync(
                StoreName,
                string.Format(KeyFormat, this.EntityName, "keys"),
                keys,
                cancellationToken: cancellationToken);
        }

        // Delete the entity
        await dbContext.DeleteStateAsync(
            StoreName,
            stateKey,
            cancellationToken: cancellationToken);
    }

    public override Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // For Dapr, changes are saved immediately, so this is a no-op
        return Task.FromResult(0);
    }

    public async override Task<List<TEntity>> GetListAsync(bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = await GetQueryableAsync();
        return await queryable.ToListAsync(cancellationToken);
    }

    public async override Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        var keys = await dbContext.GetStateAsync<List<string>>(StoreName,
            string.Format(KeyFormat, this.EntityName, "keys"),
            cancellationToken: cancellationToken) ?? new List<string>();

        return keys.Count;
    }

    public async override Task<PagedList<TEntity>> GetPagedListAsync(PaginationParameters parameters,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = await GetQueryableAsync();
        var items = await queryable
            .PageBy(parameters.SkipCount, parameters.MaxResultCount)
            .ToListAsync(cancellationToken);

        return new PagedList<TEntity>(items, queryable.Count(), parameters.SkipCount, parameters.MaxResultCount);
    }

    public async override Task<IQueryable<TEntity>> GetQueryableAsync()
    {
        // Get all keys first
        var keys = await dbContext.GetStateAsync<List<string>>(StoreName,
            string.Format(KeyFormat, this.EntityName, "keys"));
        if (keys == null || !keys.Any())
            return Enumerable.Empty<TEntity>().AsQueryable();

        // Get all items in bulk
        var states = await dbContext.GetBulkStateAsync(StoreName, keys, parallelism: 1);

        return states
            .Where(s => s.Value != null)
            .Select(s => JsonSerializer.Deserialize<TEntity>(s.Value, JsonOptions()))
            .Where(item => item != null)
            .AsQueryable()!;
    }

    public async override Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = await GetQueryableAsync();
        return await queryable.Where(predicate).FirstOrDefaultAsync(cancellationToken);
    }

    public async override Task DeleteAsync(Expression<Func<TEntity, bool>> predicate, bool saveChanges = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = await GetQueryableAsync();
        var items = await queryable.Where(predicate).ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            await DeleteAsync(item, saveChanges, cancellationToken);
        }
    }

    public override Task DeleteDirectAsync(Expression<Func<TEntity, bool>> predicate, bool saveChanges = true,
        CancellationToken cancellationToken = default)
    {
        return DeleteAsync(predicate, saveChanges, cancellationToken);
    }

    public async override Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var queryable = await GetQueryableAsync();
        return await queryable.Where(predicate).ToListAsync(cancellationToken: cancellationToken);
    }

    protected static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}

public class DaprRepository<TDbContext, TEntity, TKey>(
    TDbContext dbContext,
    IServiceProvider serviceProvider,
    string storeName)
    : DaprRepository<TDbContext, TEntity>(dbContext, serviceProvider, storeName),
        IDaprRepository<TEntity, TKey>
    where TDbContext : DaprClient
    where TEntity : class, IEntity<TKey>
{
    private readonly TDbContext _dbContext = dbContext;

    public async Task<TEntity> GetAsync(TKey id, bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, true, cancellationToken);

        if (entity == null)
        {
            throw new EntityNotFoundException(typeof(TEntity), id);
        }

        return entity;
    }

    public async Task<TEntity?> FindAsync(TKey id, bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var stateKey = string.Format(KeyFormat, this.EntityName, id);
        return await _dbContext.GetStateAsync<TEntity>(StoreName, stateKey, cancellationToken: cancellationToken);
    }

    public async Task DeleteAsync(TKey id, bool saveChanges = true, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, cancellationToken: cancellationToken);
        if (entity == null)
        {
            return;
        }

        await DeleteAsync(entity, saveChanges, cancellationToken);
    }
}