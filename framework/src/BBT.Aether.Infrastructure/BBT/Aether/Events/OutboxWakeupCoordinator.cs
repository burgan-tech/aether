using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events;

/// <summary>
/// Decides when the outbox wakeup nudge fires: once per unit of work that stored at least one
/// outbox message, from the UoW's OnCompleted callback — but WITHOUT extending the commit path:
/// the callback returns immediately and the pub/sub publish runs as an unobserved task with a
/// bounded timeout. A lost or failed nudge is logged and absorbed by the polling safety net.
/// </summary>
public sealed class OutboxWakeupCoordinator(
    AetherOutboxOptions options,
    IUnitOfWorkManager? unitOfWorkManager = null,
    IOutboxWakeupNotifier? wakeupNotifier = null,
    ILogger<OutboxWakeupCoordinator>? logger = null)
{
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConditionalWeakTable<IUnitOfWork, object> WakeupRegistered = new();
    private static readonly object RegisteredSentinel = new();

    /// <summary>Call once per stored outbox message; registration collapses to one per UoW.</summary>
    public void OnOutboxMessageStored()
    {
        if (wakeupNotifier is null || !options.WakeupSignalEnabled)
            return;

        var uow = unitOfWorkManager?.Current;
        if (uow is null)
        {
            // No ambient UoW: the caller flushes on its own SaveChanges, which this coordinator
            // cannot observe — the nudge may land BEFORE the row is visible. This branch is an
            // early best-effort hint EXCLUDED from the latency guarantee (the row then waits for
            // normal polling). Every vnext transition path runs with an ambient UoW, so this is
            // never the latency-critical path.
            NotifyFireAndForget();
            return;
        }

        // Dedupe on the shared transaction root, not the per-call scope object. A `Required`
        // participant scope (UnitOfWorkScope with ownsRoot == false) is a distinct object per
        // nesting level that forwards OnCompleted to the same CompositeUnitOfWork root — keying
        // on `uow` itself would register (and later fire) once per nested scope for a single
        // commit. CompositeUnitOfWork itself already implements IUnitOfWork, so it is a valid
        // ConditionalWeakTable key; a `uow` that is not a UnitOfWorkScope (e.g. the root itself,
        // or a test substitute) dedupes on itself as before.
        var dedupeKey = (uow as UnitOfWorkScope)?.SharedRoot ?? uow;

        lock (RegisteredSentinel)
        {
            if (WakeupRegistered.TryGetValue(dedupeKey, out _))
                return;
            WakeupRegistered.Add(dedupeKey, RegisteredSentinel);
        }

        // OnCompleted callbacks are awaited inside CommitAsync — return a completed task and let
        // the publish run detached so a slow sidecar can never stretch the commit path.
        uow.OnCompleted(_ =>
        {
            NotifyFireAndForget();
            return Task.CompletedTask;
        });
    }

    private void NotifyFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(NotifyTimeout);
                await wakeupNotifier!.NotifyAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Outbox wakeup nudge failed or timed out; polling will pick the work up");
            }
        });
    }
}
