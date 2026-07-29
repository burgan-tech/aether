using System;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(cancellationToken: stoppingToken);
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
            }
            else
            {
                // Sleep until a wake-up signal arrives or the fallback interval elapses.
                // Fallback polling stays the correctness mechanism and is never disabled —
                // a lost signal must cost latency, never data.
                //
                // The returned keys are deliberately ignored: which partition was signalled
                // only becomes actionable when partition-filtered leasing lands in a later
                // phase. Until then any signal simply means "poll now".
                await signalCoordinator.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
