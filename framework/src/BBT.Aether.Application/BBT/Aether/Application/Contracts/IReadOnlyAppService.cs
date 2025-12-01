using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Aether.Application.Dtos;
using BBT.Aether.Domain;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Application;

/// <summary>
/// Interface for read-only application service with entity DTO and key.
/// </summary>
/// <typeparam name="TEntityDto">The type of the entity DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IReadOnlyAppService<TEntityDto, in TKey>
    : IReadOnlyAppService<TEntityDto, TEntityDto, TKey, PagedAndSortedResultRequestDto>
{

}

/// <summary>
/// Interface for read-only application service with entity DTO, key, and list input.
/// </summary>
/// <typeparam name="TEntityDto">The type of the entity DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TGetListInput">The type of the list input.</typeparam>
public interface IReadOnlyAppService<TEntityDto, in TKey, in TGetListInput>
    : IReadOnlyAppService<TEntityDto, TEntityDto, TKey, TGetListInput>
{

}

/// <summary>
/// Interface for read-only application service with output DTOs, key, and list input.
/// </summary>
/// <typeparam name="TGetOutputDto">The type of the get output DTO.</typeparam>
/// <typeparam name="TGetListOutputDto">The type of the get list output DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TGetListInput">The type of the list input.</typeparam>
public interface IReadOnlyAppService<TGetOutputDto, TGetListOutputDto, in TKey, in TGetListInput>
    : IApplicationService
{
    /// <summary>
    /// Gets an entity by its key.
    /// </summary>
    /// <param name="id">The key of the entity.</param>
    /// <returns>The entity DTO.</returns>
    Task<TGetOutputDto> GetAsync(TKey id);

    /// <summary>
    /// Gets a list of entities based on the input parameters.
    /// </summary>
    /// <param name="input">The input parameters for getting the list.</param>
    /// <returns>A paged result DTO containing the list of entities.</returns>
    Task<PagedResultDto<TGetListOutputDto>> GetListAsync(TGetListInput input);
}

/// <summary>
/// Interface for read-only entity application service with entity and key.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IReadOnlyEntityAppService<TEntity, in TKey>
    : IApplicationService
{
    /// <summary>
    /// Gets an entity by its key.
    /// </summary>
    /// <param name="id">The key of the entity.</param>
    /// <returns>The entity.</returns>
    Task<TEntity> GetAsync(TKey id);

    /// <summary>
    /// Gets a paged list of entities based on the input parameters.
    /// </summary>
    /// <param name="input">The pagination parameters.</param>
    /// <returns>A paged list of entities.</returns>
    Task<PagedList<TEntity>> GetPagedListAsync(PaginationParameters input);
    
    /// <summary>
    /// Gets a all list of entities based.
    /// </summary>
    /// <returns>A all list of entities.</returns>
    Task<IList<TEntity>> GetListAsync();
}