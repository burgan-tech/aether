namespace BBT.Aether.Application;

/// <summary>
/// Defines the base interface for an application service that supports both create and update operations with the same DTO for both operations.
/// </summary>
/// <typeparam name="TEntityDto">The DTO type.</typeparam>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public interface ICreateUpdateAppService<TEntityDto, in TKey>
    : ICreateUpdateAppService<TEntityDto, TKey, TEntityDto, TEntityDto>
{

}

/// <summary>
/// Defines the base interface for an application service that supports both create and update operations with a specific input DTO for both operations.
/// </summary>
/// <typeparam name="TEntityDto">The DTO type.</typeparam>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
/// <typeparam name="TCreateUpdateInput">The type of the input DTO for create and update operations.</typeparam>
public interface ICreateUpdateAppService<TEntityDto, in TKey, in TCreateUpdateInput>
    : ICreateUpdateAppService<TEntityDto, TKey, TCreateUpdateInput, TCreateUpdateInput>
{

}

/// <summary>
/// Defines the base interface for an application service that supports both create and update operations with different input DTOs.
/// </summary>
/// <typeparam name="TGetOutputDto">The DTO type returned after create or update.</typeparam>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
/// <typeparam name="TCreateUpdateInput">The type of the input DTO for create operation.</typeparam>
/// <typeparam name="TUpdateInput">The type of the input DTO for update operation.</typeparam>
public interface ICreateUpdateAppService<TGetOutputDto, in TKey, in TCreateUpdateInput, in TUpdateInput>
    : ICreateAppService<TGetOutputDto, TCreateUpdateInput>,
        IUpdateAppService<TGetOutputDto, TKey, TUpdateInput>
{

}

/// <summary>
/// Defines the base interface for an application service that supports both create and update operations directly on entities.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public interface ICreateUpdateEntityAppService<TEntity, in TKey>
    : ICreateAppService<TEntity, TEntity>,
        IUpdateAppService<TEntity, TKey, TEntity>
{

}