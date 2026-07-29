using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxBackgroundService(
    IOutboxProcessor processor,
    IOutboxSignalCoordinator signalCoordinator,
    AetherOutboxOptions options,
    ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = options.IdlePollingInterval;
        var backingOffAfterError = false;
        IReadOnlyCollection<short>? partitions = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(partitions, stoppingToken);
                delay = AdaptivePolling.NextDelay(
                    delay, processed, options.BatchSize,
                    options.BusyPollingInterval, options.IdlePollingInterval, options.MaxPollingInterval);
                backingOffAfterError = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox background service error");
                delay = options.ErrorPollingInterval;
                backingOffAfterError = true;
            }

            if (backingOffAfterError)
            {
                // A signal must not cut an error back-off short. The back-off protects a
                // database we have just failed to reach, and a signal only says work exists —
                // it cannot say the target has recovered. Honouring one here would drive the
                // dispatcher straight back at a struggling database, and with signals arriving
                // continuously the back-off would collapse to nothing.
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                // Signals that arrived during the back-off were never read, so the next attempt
                // must sweep everything rather than trusting a filter from before the failure.
                partitions = null;
            }
            else
            {
                // Sleep until a wake-up signal arrives or the fallback interval elapses.
                // Fallback polling stays the correctness mechanism and is never disabled —
                // a lost signal must cost latency, never data.
                //
                // Which partitions were signalled narrows the next cycle's lease. An empty
                // result means the fallback timeout fired, and PartitionFilter turns that into
                // an unfiltered sweep — that sweep is what recovers a partition whose signal
                // was lost.
                var keys = await signalCoordinator.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                partitions = PartitionFilter.Resolve(keys);
            }
        }
    }
}
