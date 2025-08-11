using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Application.Dtos;
using BBT.Aether.Auditing;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Application.Services;

public abstract class CrudAppService<TEntity, TEntityDto, TKey>(
    IServiceProvider serviceProvider,
    IRepository<TEntity, TKey> repository)
    : CrudAppService<TEntity, TEntityDto, TKey, PagedAndSortedResultRequestDto>(serviceProvider, repository)
    where TEntity : class, IEntity<TKey>;

public abstract class CrudAppService<TEntity, TEntityDto, TKey, TGetListInput>(
    IServiceProvider serviceProvider,
    IRepository<TEntity, TKey> repository)
    : CrudAppService<TEntity, TEntityDto, TKey, TGetListInput, TEntityDto>(serviceProvider, repository)
    where TEntity : class, IEntity<TKey>;

public abstract class CrudAppService<TEntity, TEntityDto, TKey, TGetListInput, TCreateInput>(
    IServiceProvider serviceProvider,
    IRepository<TEntity, TKey> repository)
    : CrudAppService<TEntity, TEntityDto, TKey, TGetListInput, TCreateInput, TCreateInput>(serviceProvider, repository)
    where TEntity : class, IEntity<TKey>;

public abstract class CrudAppService<TEntity, TEntityDto, TKey, TGetListInput, TCreateInput, TUpdateInput>(
    IServiceProvider serviceProvider,
    IRepository<TEntity, TKey> repository)
    : CrudAppService<TEntity, TEntityDto, TEntityDto, TKey, TGetListInput, TCreateInput, TUpdateInput>(serviceProvider,
        repository)
    where TEntity : class, IEntity<TKey>
{
    protected override Task<TEntityDto> MapToGetListOutputDtoAsync(TEntity entity)
    {
        return MapToGetOutputDtoAsync(entity);
    }

    protected override TEntityDto MapToGetListOutputDto(TEntity entity)
    {
        return MapToGetOutputDto(entity);
    }
}

public abstract class CrudAppService<TEntity, TGetOutputDto, TGetListOutputDto, TKey, TGetListInput, TCreateInput,
    TUpdateInput>(IServiceProvider serviceProvider, IRepository<TEntity, TKey> repository)
    : AbstractKeyCrudAppService<TEntity, TGetOutputDto, TGetListOutputDto, TKey, TGetListInput, TCreateInput,
        TUpdateInput>(serviceProvider, repository)
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

    protected override void MapToEntity(TUpdateInput updateInput, TEntity entity)
    {
        if (updateInput is IEntityDto<TKey> entityDto)
        {
            entityDto.Id = entity.Id;
        }

        base.MapToEntity(updateInput, entity);
    }

    protected override IQueryable<TEntity> ApplyDefaultSorting(IQueryable<TEntity> query)
    {
        if (typeof(TEntity).IsAssignableTo<IHasCreatedAt>())
        {
            return query.OrderByDescending(e => ((IHasCreatedAt)e).CreatedAt);
        }
        else
        {
            return query.OrderByDescending(e => e.Id);
        }
    }
}

