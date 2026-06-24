using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.TestSupport;

/// <summary>
/// Test double for <see cref="IAetherDbContextProvider{TDbContext}"/> that always returns
/// the same supplied context. Used by store unit tests that operate against a single
/// in-memory context.
/// </summary>
internal sealed class FixedDbContextProvider<TDbContext>(TDbContext ctx) : IAetherDbContextProvider<TDbContext>
    where TDbContext : DbContext
{
    public Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ctx);
}
