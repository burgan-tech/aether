using BBT.Aether.Application;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.AspNetCore.Controllers;

public abstract class ReadOnlyEntityController<TEntity, TKey>(
    IReadOnlyEntityAppService<TEntity, TKey> service
)
    : AbstractKeyEntityReadOnlyController<TEntity, TKey>(
        service
    ) where TEntity : class, IEntity<TKey>
{
}