using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Defines the interface for the inbox processor.
/// Cleans up old processed inbox messages.
/// </summary>
public interface IInboxProcessor
{
    /// <summary>
    /// Runs the inbox processor with the specified cancellation token.
    /// This method contains the processing loop.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RunAsync(CancellationToken cancellationToken = default);
}

