using BBT.Aether.Application;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.AspNetCore.Controllers;

public abstract class CrudEntityController<TEntity, TKey>(ICrudEntityAppService<TEntity, TKey> service)
    : AbstractKeyEntityCrudController<TEntity, TKey>(service) where TEntity : class, IEntity<TKey>
{
}