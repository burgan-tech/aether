using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Defines the interface for the inbox processor.</summary>
public interface IInboxProcessor
{
    /// <summary>
    /// Runs one processing cycle. Returns the number of messages processed.
    /// </summary>
    /// <remarks>
    /// Does not swallow failures: an exception from the underlying processing or cleanup work
    /// propagates to the caller so it can decide how to back off. Callers must not treat a thrown
    /// exception as "zero messages processed".
    /// </remarks>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
