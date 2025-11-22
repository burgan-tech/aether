using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
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
    private readonly TDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the EfCoreJobStore class.
    /// </summary>
    /// <param name="dbContext">The database context for job persistence.</param>
    public EfCoreJobStore(TDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc/>
    public async Task SaveAsync(BackgroundJobInfo jobInfo, CancellationToken cancellationToken = default)
    {
        if (jobInfo == null)
            throw new ArgumentNullException(nameof(jobInfo));

        // Check if job already exists
        var existingJob = await _dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == jobInfo.Id, cancellationToken);

        if (existingJob != null)
        {
            // Update existing job
            _dbContext.Entry(existingJob).CurrentValues.SetValues(jobInfo);
            existingJob.ExtraProperties = jobInfo.ExtraProperties;
            existingJob.Payload = jobInfo.Payload;
        }
        else
        {
            // Insert new job
            await _dbContext.BackgroundJobs.AddAsync(jobInfo, cancellationToken);
        }

        // SaveChanges removed - will be flushed by UoW Commit or calling code
    }

    /// <inheritdoc/>
    public async Task<BackgroundJobInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id cannot be empty.", nameof(id));

        return await _dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<BackgroundJobInfo?> GetByJobNameAsync(string jobName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            throw new ArgumentNullException(nameof(jobName));

        return await _dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.JobName == jobName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<BackgroundJobInfo>> GetByHandlerNameAsync(string handlerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new ArgumentNullException(nameof(handlerName));

        return await _dbContext.BackgroundJobs
            .Where(j => j.HandlerName == handlerName)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<BackgroundJobInfo>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.BackgroundJobs
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

        var job = await _dbContext.BackgroundJobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
            throw new InvalidOperationException($"Job with id '{id}' not found.");

        job.Status = status;
        
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

