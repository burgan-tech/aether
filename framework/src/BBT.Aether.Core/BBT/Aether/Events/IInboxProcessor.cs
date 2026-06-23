using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>Defines the interface for the inbox processor.</summary>
public interface IInboxProcessor
{
    /// <summary>
    /// Runs one processing cycle. Returns the number of messages processed.
    /// </summary>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
