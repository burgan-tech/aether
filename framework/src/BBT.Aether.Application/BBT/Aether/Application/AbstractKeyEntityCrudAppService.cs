using System;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Application.Services;


public abstract class AbstractKeyEntityCrudAppService<TEntity, TKey>(IServiceProvider serviceProvider, IRepository<TEntity> repository)
    : AbstractKeyEntityReadOnlyAppService<TEntity, TKey>(serviceProvider, repository),
        ICrudEntityAppService<TEntity, TKey>
    where TEntity : class, IEntity
{
    protected IRepository<TEntity> Repository { get; } = repository;

    public virtual async Task<TEntity> CreateAsync(TEntity input)
    {
        SetIdForGuids(input);
        await Repository.InsertAsync(input);
        return input;
    }

    public virtual async Task<TEntity> UpdateAsync(TKey id, TEntity input)
    {
        await Repository.UpdateAsync(input, true);
        return input;
    }

    public virtual async Task DeleteAsync(TKey id)
    {
        await DeleteByIdAsync(id);
    }

    protected abstract Task DeleteByIdAsync(TKey id);

    /// <summary>
    /// Sets ID value for the entity if <typeparamref name="TKey"/> is <see cref="Guid"/>.
    /// It's used while creating a new entity.
    /// </summary>
    protected virtual void SetIdForGuids(TEntity entity)
    {
        if (entity is IEntity<Guid> entityWithGuidId && entityWithGuidId.Id == Guid.Empty)
        {
            EntityHelper.TrySetId(
                entityWithGuidId,
                () => GuidGenerator.Create(),
                true
            );
        }
    }
}