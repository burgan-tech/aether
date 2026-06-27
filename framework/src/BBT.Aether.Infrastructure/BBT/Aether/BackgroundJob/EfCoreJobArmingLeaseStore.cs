using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// EF Core fallback implementation of <see cref="IJobArmingLeaseStore"/>. Claims due jobs
/// with a per-row <c>WHERE ArmingToken IS NULL</c> guard — no row-level locking.
/// Suitable for SQL Server or single-pod deployments. In multi-pod PostgreSQL use
/// <c>NpgsqlJobArmingLeaseStore</c> which adds <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
public class EfCoreJobArmingLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IJobArmingLeaseStore
    where TDbContext : DbContext, IHasEfCoreBackgroundJobs
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<BackgroundJobArmingClaim>> ClaimBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<int>? partitionNos = null,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var now = clock.UtcNow;
        var armingUntil = now.Add(leaseDuration);
        var armingToken = Guid.NewGuid();

        var query = dbContext.BackgroundJobs
            .Where(j => (j.Status == BackgroundJobStatus.Pending
                         || (j.Status == BackgroundJobStatus.Retrying
                             && j.NextRetryAt != null && j.NextRetryAt <= now))
                        && (j.ArmingToken == null || j.ArmingUntil < now));

        if (partitionNos != null && partitionNos.Count > 0)
            query = query.Where(j => partitionNos.Contains(j.PartitionNo));

        var candidates = await query
            .OrderBy(j => j.NextRetryAt ?? DateTime.MinValue)
            .ThenBy(j => j.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        // workerId is not persisted to the row — it is available for logging/diagnostics only.
        var claims = new List<BackgroundJobArmingClaim>(candidates.Count);
        foreach (var job in candidates)
        {
            // Per-row atomic claim: only succeeds if still unclaimed
            var affected = await dbContext.BackgroundJobs
                .Where(j => j.Id == job.Id
                            && (j.ArmingToken == null || j.ArmingUntil < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.ArmingToken, armingToken)
                    .SetProperty(j => j.ArmingUntil, armingUntil)
                    .SetProperty(j => j.ModifiedAt, now),
                    cancellationToken);

            if (affected > 0)
                claims.Add(new BackgroundJobArmingClaim(job, job.Status, armingToken));
        }

        return claims;
    }
}
