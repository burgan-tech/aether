using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Entity Framework Core implementation of the job store.
/// Provides persistence operations for background jobs using EF Core and integrates with UoW pattern.
/// </summary>
/// <typeparam name="TDbContext">The DbContext type that implements IHasEfCoreBackgroundJobs</typeparam>
public class EfCoreJobStore<TDbContext> : IJobStore
    where TDbContext : DbContext, IHasEfCoreBackgroundJobs
{
    private readonly IAetherDbContextProvider<TDbContext> _dbContextProvider;

    /// <summary>
    /// Initializes a new instance of the EfCoreJobStore class.
    /// </summary>
    /// <param name="dbContextProvider">Provider resolving the schema-bound database context for job persistence.</param>
    public EfCoreJobStore(IAetherDbContextProvider<TDbContext> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider ?? throw new ArgumentNullException(nameof(dbContextProvider));
    }

    /// <inheritdoc/>
    public async Task SaveAsync(BackgroundJobInfo jobInfo, CancellationToken cancellationToken = default)
    {
        if (jobInfo == null)
            throw new ArgumentNullException(nameof(jobInfo));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        // Id-based upsert: look up by the entity primary key so the operation works for a job in ANY
        // status (not just Scheduled/Running) and so every persistent field is preserved on update.
        var existingJob = await dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == jobInfo.Id, cancellationToken);

        if (existingJob == null)
        {
            // Insert new job
            await dbContext.BackgroundJobs.AddAsync(jobInfo, cancellationToken);
            return;
        }

        // Already the tracked instance (mutated in place) — nothing to copy.
        if (ReferenceEquals(existingJob, jobInfo))
            return;

        // Update mutable fields only — never touch the key (Id) or creation audit.
        existingJob.ExpressionValue = jobInfo.ExpressionValue;
        existingJob.Payload = jobInfo.Payload;
        existingJob.Status = jobInfo.Status;
        existingJob.Kind = jobInfo.Kind;
        existingJob.MaxRetryCount = jobInfo.MaxRetryCount;
        existingJob.NextRetryAt = jobInfo.NextRetryAt;
        existingJob.LastRunAt = jobInfo.LastRunAt;
        existingJob.HandledTime = jobInfo.HandledTime;
        existingJob.RetryCount = jobInfo.RetryCount;
        existingJob.LastError = jobInfo.LastError;
        existingJob.ExtraProperties = jobInfo.ExtraProperties;
        existingJob.ModifiedAt = DateTime.UtcNow;

        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    /// <inheritdoc/>
    public async Task<BackgroundJobInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<BackgroundJobInfo?> GetByJobNameAsync(string jobName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.JobName == jobName
                    && (j.Status == BackgroundJobStatus.Scheduled || j.Status == BackgroundJobStatus.Running),
                cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<BackgroundJobInfo>> GetByHandlerNameAsync(string handlerName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.BackgroundJobs
            .Where(j => j.HandlerName == handlerName)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<BackgroundJobInfo>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.BackgroundJobs
            .Where(j => j.Status == BackgroundJobStatus.Scheduled || j.Status == BackgroundJobStatus.Running)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateStatusAsync(
        Guid id,
        BackgroundJobStatus status,
        DateTime? handledTime = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        var job = await dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
            throw new InvalidOperationException($"Job with id '{id}' not found.");

        job.Status = status;
        job.ModifiedAt = DateTime.UtcNow;
        if (handledTime.HasValue)
        {
            job.HandledTime = handledTime.Value;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            job.LastError = error;
        }

        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    /// <inheritdoc/>
    public async Task<bool> TryTransitionStatusAsync(Guid id, BackgroundJobStatus from, BackgroundJobStatus to,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        // Conditional UPDATE: provider-agnostic optimistic-concurrency guard. The WHERE clause pins
        // the current status, so concurrent claims race on a single atomic row update — exactly one
        // wins. Flows through the UoW's shared connection (search_path interceptor sets schema first).
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == id && j.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, to), cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BackgroundJobInfo>> GetDueForArmingAsync(DateTime nowUtc, int batchSize,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.BackgroundJobs
            .Where(j => j.Status == BackgroundJobStatus.Pending
                        || (j.Status == BackgroundJobStatus.Retrying && j.NextRetryAt != null && j.NextRetryAt <= nowUtc))
            .OrderBy(j => j.NextRetryAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task MarkRetryingAsync(Guid id, DateTime nextRetryAtUtc, string? error,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        var job = await dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
            throw new InvalidOperationException($"Job with id '{id}' not found.");

        job.Status = BackgroundJobStatus.Retrying;
        job.RetryCount++;
        job.NextRetryAt = nextRetryAtUtc;
        job.LastError = error;
        // HandledTime intentionally left untouched: a retrying job has not been "handled". It is set
        // only on a terminal Completed/Failed transition via UpdateStatusAsync.
        job.ModifiedAt = DateTime.UtcNow;

        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    /// <inheritdoc/>
    public async Task MarkRecurringRanAsync(Guid id, DateTime ranAtUtc, string? error,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        var job = await dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
            throw new InvalidOperationException($"Job with id '{id}' not found.");

        job.Status = BackgroundJobStatus.Scheduled;
        job.LastRunAt = ranAtUtc;
        if (error != null)
        {
            job.LastError = error;
            job.RetryCount++;
        }
        job.ModifiedAt = DateTime.UtcNow;

        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    /// <inheritdoc/>
    public async Task<bool> TryClaimAsync(Guid id, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        // Conditional UPDATE: provider-agnostic optimistic-concurrency claim. The WHERE clause pins
        // Status=Scheduled, so concurrent claims race on a single atomic row update — exactly one wins,
        // and that winner also stamps RunningSince for the visibility-timeout reaper.
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == id && j.Status == BackgroundJobStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Running)
                .SetProperty(j => j.RunningSince, nowUtc), cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BackgroundJobInfo>> GetStaleRunningAsync(DateTime cutoffUtc, int batchSize,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
        return await dbContext.BackgroundJobs
            .Where(j => j.Status == BackgroundJobStatus.Running && j.RunningSince != null && j.RunningSince < cutoffUtc)
            .OrderBy(j => j.RunningSince)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
}