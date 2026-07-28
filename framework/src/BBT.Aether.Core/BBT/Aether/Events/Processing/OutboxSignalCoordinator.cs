using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// In-memory coordinator coalescing wake-up signals for the dispatcher loop.
/// </summary>
public sealed class OutboxSignalCoordinator : IOutboxSignalCoordinator
{
    private readonly ConcurrentDictionary<OutboxSignalKey, byte> _pending = new();

    // Capacity 1 with DropWrite: the channel is only a doorbell. Extra rings while one is
    // already pending are redundant — the pending set carries the actual information.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public void Signal(string schema, short partitionId)
    {
        _pending.TryAdd(new OutboxSignalKey(schema, partitionId), 0);
        _wake.Writer.TryWrite(true);
    }

    public async Task<IReadOnlyCollection<OutboxSignalKey>> WaitAsync(
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
        {
            // Benign race: a signal can land here, between the IsEmpty check and the
            // ReadAsync below. It is not lost — Signal() already wrote to the pending
            // dictionary and tried to post to the channel before this method observed
            // IsEmpty, so the ReadAsync call immediately below will pick up that post
            // (or, if the write already landed and was read away, the loop simply falls
            // through to the drain below on the *next* WaitAsync call after the fallback
            // timeout). Worst case: one extra wait cycle, never a lost signal.
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await _wake.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fallback timeout — expected, not an error.
            }
        }

        // Collapse any extra doorbell rings.
        while (_wake.Reader.TryRead(out _)) { }

        var keys = _pending.Keys.ToArray();
        foreach (var key in keys) _pending.TryRemove(key, out _);

        // Benign race, part two: a key drained here can be re-signalled immediately by a
        // transaction committing at the same moment (Signal() runs concurrently with this
        // drain). That produces one extra check on the next cycle — a redundant poll, never
        // a lost row, because the next WaitAsync call will see the pending set non-empty
        // again and return immediately.
        return keys;
    }
}
