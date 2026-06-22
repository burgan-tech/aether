using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Domain.EntityFrameworkCore;

/// <summary>
/// Resolves the schema-bound <typeparamref name="TDbContext"/> for the current request/job:
/// it reads the active schema from <see cref="BBT.Aether.MultiSchema.ICurrentSchema"/> and asks
/// the active UnitOfWork to materialize (and cache) the context bound to that schema on the
/// unit of work's shared connection/transaction.
/// </summary>
public interface IAetherDbContextProvider<TDbContext> where TDbContext : DbContext
{
    Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default);
}
