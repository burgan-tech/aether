using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.DistributedLock;

/// <summary>
/// Represents an acquired distributed lock scope.
/// Provides automatic release via <see cref="IAsyncDisposable"/>.
/// </summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>
    /// Gets the resource identifier that this lock protects.
    /// </summary>
    string LockKey { get; }

    /// <summary>
    /// Gets the unique owner identifier for this lock acquisition.
    /// </summary>
    string Owner { get; }

    /// <summary>
    /// Extends the lock TTL. Use for long-running operations that may exceed the initial lease.
    /// </summary>
    /// <param name="leaseSeconds">New TTL in seconds from now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the TTL was successfully extended; <c>false</c> if the lock was lost.</returns>
    Task<bool> ExtendAsync(int leaseSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly releases the lock. <see cref="IAsyncDisposable.DisposeAsync"/> also releases.
    /// Safe to call multiple times.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}
