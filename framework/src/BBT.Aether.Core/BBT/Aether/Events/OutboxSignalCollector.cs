using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events;

/// <summary>
/// Coalesces wake-up signals per unit of work and publishes them after commit.
/// </summary>
public sealed class OutboxSignalCollector(
    IUnitOfWorkManager unitOfWorkManager,
    IOutboxWakeupPublisher publisher,
    AetherOutboxOptions options,
    ILogger<OutboxSignalCollector> logger) : IOutboxSignalCollector
{
    /// <summary>
    /// Above this fraction of the configured partitions in one transaction, a single check-all
    /// signal is cheaper for the broker than one signal each. Derived from
    /// <see cref="AetherOutboxOptions.PartitionCount"/> so it stays sensible when partitioning
    /// is tuned down — setting PartitionCount to 1 turns partitioning off entirely.
    /// </summary>
    private int CollapseThreshold => Math.Max(4, options.PartitionCount / 4);

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
            catch (Exception ex)
            {
                // The business transaction has already committed. A broker failure must not
                // surface here — fallback polling covers the rows regardless. Reaching this
                // catch means the publisher broke its no-throw contract (it should return
                // false, never throw), so leave a breadcrumb rather than staying silent.
                logger.LogDebug(
                    ex,
                    "Outbox wake-up publisher broke its no-throw contract for schema {Schema} partition {PartitionId}",
                    signal.Schema,
                    signal.PartitionId);
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
