using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Application.Dtos;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Application.Services;

public abstract class ReadOnlyAppService<TEntity, TEntityDto, TKey>(
    IServiceProvider serviceProvider,
    IReadOnlyRepository<TEntity, TKey> repository)
    : ReadOnlyAppService<TEntity, TEntityDto, TEntityDto, TKey, PagedAndSortedResultRequestDto>(serviceProvider,
        repository)
    where TEntity : class, IEntity<TKey>;

public abstract class ReadOnlyAppService<TEntity, TEntityDto, TKey, TGetListInput>(
    IServiceProvider serviceProvider,
    IReadOnlyRepository<TEntity, TKey> repository)
    : ReadOnlyAppService<TEntity, TEntityDto, TEntityDto, TKey, TGetListInput>(serviceProvider, repository)
    where TEntity : class, IEntity<TKey>;

public abstract class ReadOnlyAppService<TEntity, TGetOutputDto, TGetListOutputDto, TKey, TGetListInput>(
    IServiceProvider serviceProvider,
    IReadOnlyRepository<TEntity, TKey> repository)
    : AbstractKeyReadOnlyAppService<TEntity, TGetOutputDto, TGetListOutputDto, TKey, TGetListInput>(serviceProvider,
        repository)
    where TEntity : class, IEntity<TKey>
{
    protected IReadOnlyRepository<TEntity, TKey> Repository { get; } = repository;

    protected async override Task<TEntity> GetEntityByIdAsync(TKey id)
    {
        return await Repository.GetAsync(id);
    }

    protected override IQueryable<TEntity> ApplyDefaultSorting(IQueryable<TEntity> query)
    {
        if (typeof(TEntity).IsAssignableTo<ICreationAuditedObject>())
        {
            return query.OrderByDescending(e => ((ICreationAuditedObject)e).CreatedAt);
        }

        return query.OrderByDescending(e => e.Id);
    }
}