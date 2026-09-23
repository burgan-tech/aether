using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Deletes processed inbox messages whose retention period has elapsed. Implementations must be
/// safe to run from several workers at once: a row another worker is already deleting is skipped,
/// never reported as a concurrency failure.
/// </summary>
public interface IInboxCleanupStore
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> processed messages older than
    /// <paramref name="retentionPeriod"/>, oldest first.
    /// </summary>
    /// <param name="batchSize">Maximum number of messages to delete</param>
    /// <param name="retentionPeriod">Retention period for processed messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of messages deleted</returns>
    Task<int> DeleteProcessedAsync(int batchSize, TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
