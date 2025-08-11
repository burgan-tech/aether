using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore;

public interface IEfCoreRepository<TEntity> : ISupportsSavingChanges, IRepository<TEntity>
    where TEntity: class, IEntity
{
    Task<DbContext> GetDbContextAsync();

    Task<DbSet<TEntity>> GetDbSetAsync();
}

public interface IEfCoreRepository<TEntity, TKey> : IEfCoreRepository<TEntity>, IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{

}