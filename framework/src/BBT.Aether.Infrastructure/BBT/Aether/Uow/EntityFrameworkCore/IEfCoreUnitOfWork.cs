using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>
/// EF Core extension of <see cref="IUnitOfWork"/>: hands out schema-bound DbContext
/// instances that share the unit of work's single connection and transaction.
/// </summary>
public interface IEfCoreUnitOfWork : IUnitOfWork
{
    Task<TDbContext> GetDbContextAsync<TDbContext>(string schema, CancellationToken cancellationToken = default)
        where TDbContext : DbContext;
}
