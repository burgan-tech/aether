using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;

namespace BBT.Aether.Domain.Dapr;

public interface IDaprRepository<TEntity> : IRepository<TEntity>
    where TEntity : class, IEntity
{
}

public interface IDaprRepository<TEntity, TKey> : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
}