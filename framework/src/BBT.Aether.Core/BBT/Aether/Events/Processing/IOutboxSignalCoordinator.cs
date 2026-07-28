using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events.Processing;

/// <summary>Signal key: which outbox table and partition has work.</summary>
public readonly record struct OutboxSignalKey(string Schema, short PartitionId);

/// <summary>
/// Bridges incoming wake-up signals to the dispatcher loop.
/// </summary>
/// <remarks>
/// Singleton. Signalling is fire-and-forget and must never block the caller — the subscription
/// endpoint has to return promptly so the broker does not tie its retry behaviour to dispatch
/// processing.
/// </remarks>
public interface IOutboxSignalCoordinator
{
    /// <summary>Records a pending signal and wakes a waiting dispatcher, if any.</summary>
    void Signal(string schema, short partitionId);

    /// <summary>
    /// Waits for at least one signal or until <paramref name="timeout"/> elapses, then drains
    /// and returns the pending keys. An empty result means the fallback timeout fired.
    /// </summary>
    Task<IReadOnlyCollection<OutboxSignalKey>> WaitAsync(
        TimeSpan timeout, CancellationToken cancellationToken);
}
