using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Defines the interface for the outbox processor.</summary>
public interface IOutboxProcessor
{
    /// <summary>
    /// Runs one processing cycle. Returns the number of messages processed.
    /// </summary>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
