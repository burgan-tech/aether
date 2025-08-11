using System.Threading.Tasks;

namespace BBT.Aether.Application;

/// <summary>
/// Defines a service for updating entities with the same DTO type for input and output.
/// </summary>
/// <typeparam name="TEntityDto">The DTO type for the entity.</typeparam>
/// <typeparam name="TKey">The type of the entity's primary key.</typeparam>
public interface IUpdateAppService<TEntityDto, in TKey>
    : IUpdateAppService<TEntityDto, TKey, TEntityDto>
{

}

/// <summary>
/// Defines a service for updating entities with separate DTO types for input and output.
/// </summary>
/// <typeparam name="TGetOutputDto">The DTO type for the output (retrieved entity).</typeparam>
/// <typeparam name="TKey">The type of the entity's primary key.</typeparam>
/// <typeparam name="TUpdateInput">The DTO type for the input (update data).</typeparam>
public interface IUpdateAppService<TGetOutputDto, in TKey, in TUpdateInput>
    : IApplicationService
{
    /// <summary>
    /// Updates an entity asynchronously.
    /// </summary>
    /// <param name="id">The ID of the entity to update.</param>
    /// <param name="input">The data to update the entity with.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the updated entity DTO.</returns>
    Task<TGetOutputDto> UpdateAsync(TKey id, TUpdateInput input);
}

/// <summary>
/// Defines a service for updating entities directly.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The type of the entity's primary key.</typeparam>
public interface IUpdateEntityAppService<TEntity, in TKey>
    : IApplicationService
{
    /// <summary>
    /// Updates an entity asynchronously.
    /// </summary>
    /// <param name="id">The ID of the entity to update.</param>
    /// <param name="input">The updated entity data.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the updated entity.</returns>
    Task<TEntity> UpdateAsync(TKey id, TEntity input);
}