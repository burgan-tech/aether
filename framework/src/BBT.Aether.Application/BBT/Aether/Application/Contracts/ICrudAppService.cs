using BBT.Aether.Application.Dtos;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Application;

/// <summary>
/// Interface for CRUD application service with entity DTO and key.
/// </summary>
/// <typeparam name="TEntityDto">The type of the entity DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface ICrudAppService<TEntityDto, in TKey>
    : ICrudAppService<TEntityDto, TKey, PagedAndSortedResultRequestDto>
{

}

/// <summary>
/// Interface for CRUD application service with entity DTO, key, and list input.
/// </summary>
/// <typeparam name="TEntityDto">The type of the entity DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TGetListInput">The type of the list input.</typeparam>
public interface ICrudAppService<TEntityDto, in TKey, in TGetListInput>
    : ICrudAppService<TEntityDto, TKey, TGetListInput, TEntityDto>
{

}

/// <summary>
/// Interface for CRUD application service with entity DTO, key, list input, and create input.
/// </summary>
/// <typeparam name="TEntityDto">The type of the entity DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TGetListInput">The type of the list input.</typeparam>
/// <typeparam name="TCreateInput">The type of the create input.</typeparam>
public interface ICrudAppService<TEntityDto, in TKey, in TGetListInput, in TCreateInput>
    : ICrudAppService<TEntityDto, TKey, TGetListInput, TCreateInput, TCreateInput>
{

}

/// <summary>
/// Interface for CRUD application service with entity DTO, key, list input, create input, and update input.
/// </summary>
/// <typeparam name="TEntityDto">The type of the entity DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TGetListInput">The type of the list input.</typeparam>
/// <typeparam name="TCreateInput">The type of the create input.</typeparam>
/// <typeparam name="TUpdateInput">The type of the update input.</typeparam>
public interface ICrudAppService<TEntityDto, in TKey, in TGetListInput, in TCreateInput, in TUpdateInput>
    : ICrudAppService<TEntityDto, TEntityDto, TKey, TGetListInput, TCreateInput, TUpdateInput>
{

}

/// <summary>
/// Interface for CRUD application service with output DTOs, key, list input, create input, and update input.
/// </summary>
/// <typeparam name="TGetOutputDto">The type of the get output DTO.</typeparam>
/// <typeparam name="TGetListOutputDto">The type of the get list output DTO.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TGetListInput">The type of the list input.</typeparam>
/// <typeparam name="TCreateInput">The type of the create input.</typeparam>
/// <typeparam name="TUpdateInput">The type of the update input.</typeparam>
public interface ICrudAppService<TGetOutputDto, TGetListOutputDto, in TKey, in TGetListInput, in TCreateInput, in TUpdateInput>
    : IReadOnlyAppService<TGetOutputDto, TGetListOutputDto, TKey, TGetListInput>,
        ICreateUpdateAppService<TGetOutputDto, TKey, TCreateInput, TUpdateInput>,
        IDeleteAppService<TKey>
{

}

/// <summary>
/// Interface for CRUD application service with entity and key.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface ICrudEntityAppService<TEntity, in TKey>
    : IReadOnlyEntityAppService<TEntity, TKey>,
        ICreateUpdateEntityAppService<TEntity, TKey>,
        IDeleteAppService<TKey>
{

}