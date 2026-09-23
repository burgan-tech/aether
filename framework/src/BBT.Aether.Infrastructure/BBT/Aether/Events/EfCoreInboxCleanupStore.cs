using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

/// <summary>
/// Provider-agnostic fallback implementation of <see cref="IInboxCleanupStore"/>. Deletes with a
/// set-based statement, so a row another worker already deleted simply is not counted instead of
/// raising an optimistic-concurrency failure. Overridden by <c>NpgsqlInboxCleanupStore</c>, which
/// adds <c>FOR UPDATE SKIP LOCKED</c> so concurrent workers delete disjoint batches.
/// </summary>
/// <remarks>
/// The delete runs immediately, inside the ambient unit of work's transaction when there is one.
/// </remarks>
public class EfCoreInboxCleanupStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IInboxCleanupStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    /// <inheritdoc />
    public async Task<int> DeleteProcessedAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var cutoffDate = clock.UtcNow - retentionPeriod;

        // EF Core does not support LIMIT inside ExecuteDeleteAsync, so read IDs first.
        var ids = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Processed &&
                        m.HandledTime != null &&
                        m.HandledTime < cutoffDate)
            .OrderBy(m => m.HandledTime)
            .Take(batchSize)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0) return 0;

        return await dbContext.InboxMessages
            .Where(m => ids.Contains(m.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
