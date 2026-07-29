using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Defines the interface for the outbox processor.</summary>
public interface IOutboxProcessor
{
    /// <summary>
    /// Runs one processing cycle. Returns the number of messages processed.
    /// </summary>
    /// <param name="partitionIds">
    /// Partitions a wake-up signal named, or null to lease unfiltered. Fallback polling always
    /// passes null so a partition whose signal was lost is never stranded.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// Does not swallow failures: an exception from the underlying processing or cleanup work
    /// propagates to the caller so it can decide how to back off. Callers must not treat a thrown
    /// exception as "zero messages processed".
    /// </remarks>
    Task<int> RunAsync(
        IReadOnlyCollection<short>? partitionIds = null,
        CancellationToken cancellationToken = default);
}
