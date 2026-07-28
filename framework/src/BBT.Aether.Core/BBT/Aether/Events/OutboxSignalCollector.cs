using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Uow;

namespace BBT.Aether.Events;

/// <summary>
/// Coalesces wake-up signals per unit of work and publishes them after commit.
/// </summary>
public sealed class OutboxSignalCollector(
    IUnitOfWorkManager unitOfWorkManager,
    IOutboxWakeupPublisher publisher,
    AetherOutboxOptions options) : IOutboxSignalCollector
{
    /// <summary>
    /// Above this many distinct partitions in one transaction, a single check-all signal is
    /// cheaper for the broker than one signal each.
    /// </summary>
    private const int CollapseThreshold = 16;

    private readonly HashSet<(string Schema, short PartitionId)> _pending = [];
    private bool _hookRegistered;

    public void Mark(string schema, short partitionId)
    {
        if (!options.SignalEnabled) return;

        _pending.Add((schema, partitionId));
        RegisterCommitHookOnce();
    }

    private void RegisterCommitHookOnce()
    {
        if (_hookRegistered) return;

        var uow = unitOfWorkManager.Current;
        if (uow is null) return;   // no ambient transaction; nothing to hook

        uow.OnCompleted(_ => PublishPendingAsync());
        _hookRegistered = true;
    }

    private async Task PublishPendingAsync()
    {
        if (_pending.Count == 0) return;

        var signals = BuildSignals();
        _pending.Clear();

        foreach (var signal in signals)
        {
            try
            {
                await publisher.TryPublishAsync(signal).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The business transaction has already committed. A broker failure must not
                // surface here — fallback polling covers the rows regardless.
            }
        }
    }

    private List<OutboxWakeupSignal> BuildSignals()
    {
        var emittedAt = DateTimeOffset.UtcNow;

        if (_pending.Count > CollapseThreshold)
        {
            return _pending
                .Select(p => p.Schema)
                .Distinct()
                .Select(s => new OutboxWakeupSignal(s, OutboxWakeupSignal.AllPartitions, "application", emittedAt))
                .ToList();
        }

        return _pending
            .Select(p => new OutboxWakeupSignal(p.Schema, p.PartitionId, "application", emittedAt))
            .ToList();
    }
}
