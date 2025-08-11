using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Application.Dtos;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Application.Services;

public abstract class CrudEntityAppService<TEntity, TKey>(
    IServiceProvider serviceProvider,
    IRepository<TEntity, TKey> repository)
    : AbstractKeyEntityCrudAppService<TEntity, TKey>(serviceProvider, repository)
    where TEntity : class, IEntity<TKey>
{
    protected new IRepository<TEntity, TKey> Repository { get; } = repository;

    protected async override Task DeleteByIdAsync(TKey id)
    {
        await Repository.DeleteAsync(id);
    }

    protected async override Task<TEntity> GetEntityByIdAsync(TKey id)
    {
        return await Repository.GetAsync(id);
    }

    protected override IQueryable<TEntity> ApplyDefaultSorting(IQueryable<TEntity> query)
    {
        if (typeof(TEntity).IsAssignableTo<IHasCreatedAt>())
        {
            return query.OrderByDescending(e => ((IHasCreatedAt)e).CreatedAt);
        }

        return query.OrderByDescending(e => e.Id);
    }
}