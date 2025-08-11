using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.BackgroundJob;


/// <summary>
/// Interface for background job service.
/// </summary>
public interface IBackgroundJobService
{
    /// <summary>
    /// Enqueues a background job with the specified arguments and delay.
    /// </summary>
    /// <typeparam name="TOpts">The type of the job arguments.</typeparam>
    /// <typeparam name="TJob">The type of the job handler.</typeparam>
    /// <typeparam name="TArgs">The type of the job arguments.</typeparam>
    /// <param name="args">The job arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ID of the enqueued job.</returns>
    Task<Guid> EnqueueAsync<TOpts, TJob, TArgs>(TOpts args, CancellationToken cancellationToken = default) where TJob : IBackgroundJobHandler<TArgs>;

    /// <summary>
    /// Deletes a background job with the specified job ID.
    /// </summary>
    /// <param name="jobId">The ID of the job to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the job was successfully deleted, otherwise false.</returns>
    Task<bool> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);
}
