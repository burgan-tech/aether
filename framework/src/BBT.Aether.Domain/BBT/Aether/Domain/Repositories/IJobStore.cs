using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Repositories;

/// <summary>
/// Defines the contract for persisting and retrieving background job information.
/// This interface provides data access operations for managing job state,
/// scheduling information, and execution tracking.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// Saves or updates background job information in the persistent store.
    /// If a job with the same ID already exists, it will be updated; otherwise, a new record is created.
    /// </summary>
    /// <param name="jobInfo">The complete job information including payload, metadata, and scheduling details.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when jobInfo is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job cannot be saved due to storage issues.</exception>
    Task SaveAsync(BackgroundJobInfo jobInfo, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves background job information by the entity identifier.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job to retrieve.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains the job information if found; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when id is empty.</exception>
    Task<BackgroundJobInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves background job information by the job name (external scheduler identifier).
    /// </summary>
    /// <param name="jobName">The unique job name used by the external scheduler (e.g., "send-email-order-123").</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains the job information if found; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when jobName is null or empty.</exception>
    Task<BackgroundJobInfo?> GetByJobNameAsync(string jobName, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves a collection of background jobs by handler name.
    /// </summary>
    /// <param name="handlerName">The name of the handler type to retrieve (e.g., "SendEmail", "GenerateReport").</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains a collection of job information matching the handler name.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when handlerName is null or empty.</exception>
    Task<IEnumerable<BackgroundJobInfo>> GetByHandlerNameAsync(string handlerName, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves a collection of all active background jobs (Scheduled or Running status).
    /// This method is typically used for job recovery scenarios or monitoring purposes.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous retrieval operation.
    /// The result contains a collection of active job information.
    /// </returns>
    /// <remarks>
    /// Active jobs are those in Scheduled or Running status.
    /// This method excludes jobs that are Completed, Failed, or Cancelled.
    /// </remarks>
    Task<IEnumerable<BackgroundJobInfo>> GetActiveAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates the status of a background job along with related metadata.
    /// </summary>
    /// <param name="id">The unique entity identifier of the job to update.</param>
    /// <param name="status">The new status to set.</param>
    /// <param name="handledTime">The time when the job was handled (for Completed/Failed status).</param>
    /// <param name="error">The error message (for Failed status).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous update operation.</returns>
    /// <exception cref="ArgumentException">Thrown when id is empty.</exception>
    Task UpdateStatusAsync(
        Guid id, 
        BackgroundJobStatus status, 
        DateTime? handledTime = null, 
        string? error = null, 
        CancellationToken cancellationToken = default);
}

