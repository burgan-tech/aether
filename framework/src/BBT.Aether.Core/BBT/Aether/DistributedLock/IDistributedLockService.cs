using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.DistributedLock;

/// <summary>
/// Interface for distributed lock operations
/// </summary>
public interface IDistributedLockService
{
    /// <summary>
    /// Tries to acquire a lock with the specified resource ID
    /// </summary>
    /// <param name="resourceId">The resource ID to lock</param>
    /// <param name="expiryInSeconds">Lock expiry time in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lock was acquired, false otherwise</returns>
    Task<IAsyncDisposable?> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 60, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lock with the specified resource ID
    /// </summary>
    /// <param name="resourceId">The resource ID to unlock</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lock was released, false otherwise</returns>
    Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a function within a distributed lock
    /// </summary>
    /// <typeparam name="T">Return type of the function</typeparam>
    /// <param name="resourceId">The resource ID to lock</param>
    /// <param name="function">The function to execute within the lock</param>
    /// <param name="expiryInSeconds">Lock expiry time in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the function execution, or default if lock couldn't be acquired</returns>
    Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 60, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an action within a distributed lock
    /// </summary>
    /// <param name="resourceId">The resource ID to lock</param>
    /// <param name="action">The action to execute within the lock</param>
    /// <param name="expiryInSeconds">Lock expiry time in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the action was executed successfully, false if lock couldn't be acquired</returns>
    Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 60, CancellationToken cancellationToken = default);
}
