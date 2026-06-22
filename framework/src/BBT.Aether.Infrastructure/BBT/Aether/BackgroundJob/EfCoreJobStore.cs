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

        // Check if job already exists
        var existingJob = await GetByJobNameAsync(jobInfo.JobName, cancellationToken);

        if (existingJob != null)
        {
            // Update mutable fields only — never touch the key (Id) or creation audit.
            // Copying Id via SetValues onto a tracked entity throws:
            // "The property 'BackgroundJobInfo.Id' is part of a key and so cannot be modified".
            existingJob.ExpressionValue = jobInfo.ExpressionValue;
            existingJob.Payload = jobInfo.Payload;
            existingJob.Status = jobInfo.Status;
            existingJob.HandledTime = jobInfo.HandledTime;
            existingJob.RetryCount = jobInfo.RetryCount;
            existingJob.LastError = jobInfo.LastError;
            existingJob.ExtraProperties = jobInfo.ExtraProperties;
            existingJob.ModifiedAt = DateTime.UtcNow;
        }
        else
        {
            // Insert new job
            await dbContext.BackgroundJobs.AddAsync(jobInfo, cancellationToken);
        }

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
}