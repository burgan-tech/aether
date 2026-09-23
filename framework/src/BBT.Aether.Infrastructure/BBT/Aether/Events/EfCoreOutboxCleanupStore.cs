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
/// Provider-agnostic fallback implementation of <see cref="IOutboxCleanupStore"/>. Deletes with a
/// set-based statement, so a row another worker already deleted simply is not counted instead of
/// raising an optimistic-concurrency failure. Overridden by <c>NpgsqlOutboxCleanupStore</c>, which
/// adds <c>FOR UPDATE SKIP LOCKED</c> so concurrent workers delete disjoint batches.
/// </summary>
/// <remarks>
/// The delete runs immediately, inside the ambient unit of work's transaction when there is one.
/// </remarks>
public class EfCoreOutboxCleanupStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IOutboxCleanupStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    /// <inheritdoc />
    public async Task<int> DeleteProcessedAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var cutoffDate = clock.UtcNow - retentionPeriod;

        // EF Core does not support LIMIT inside ExecuteDeleteAsync, so read IDs first.
        var ids = await dbContext.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Processed &&
                        m.ProcessedAt != null &&
                        m.ProcessedAt < cutoffDate)
            .OrderBy(m => m.ProcessedAt)
            .Take(batchSize)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0) return 0;

        return await dbContext.OutboxMessages
            .Where(m => ids.Contains(m.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
