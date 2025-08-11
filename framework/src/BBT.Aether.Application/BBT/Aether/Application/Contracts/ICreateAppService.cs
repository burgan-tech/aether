using System.Threading.Tasks;

namespace BBT.Aether.Application;

/// <summary>
/// Defines a service for creating entities with the same DTO type for input and output.
/// </summary>
/// <typeparam name="TEntityDto">The DTO type for the entity.</typeparam>
public interface ICreateAppService<TEntityDto>
    : ICreateAppService<TEntityDto, TEntityDto>
{

}

/// <summary>
/// Defines a service for creating entities with separate DTO types for input and output.
/// </summary>
/// <typeparam name="TGetOutputDto">The DTO type for the output (created entity).</typeparam>
/// <typeparam name="TCreateInput">The DTO type for the input (creation data).</typeparam>
public interface ICreateAppService<TGetOutputDto, in TCreateInput>
    : IApplicationService
{
    /// <summary>
    /// Creates a new entity asynchronously.
    /// </summary>
    /// <param name="input">The data to create the entity with.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created entity DTO.</returns>
    Task<TGetOutputDto> CreateAsync(TCreateInput input);
}

/// <summary>
/// Defines a service for creating entities directly.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface ICreateEntityAppService<TEntity>
    : IApplicationService
{
    /// <summary>
    /// Creates a new entity asynchronously.
    /// </summary>
    /// <param name="input">The entity to create.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created entity.</returns>
    Task<TEntity> CreateAsync(TEntity input);
}