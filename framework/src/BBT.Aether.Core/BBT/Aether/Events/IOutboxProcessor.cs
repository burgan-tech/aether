using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for the outbox processor.
/// Processes outbox messages and cleans up old processed messages.
/// </summary>
public interface IOutboxProcessor
{
    /// <summary>
    /// Runs the outbox processor with the specified cancellation token.
    /// This method contains the processing loop.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RunAsync(CancellationToken cancellationToken = default);
}

