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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(stoppingToken);
                delay = AdaptivePolling.NextDelay(
                    delay, processed, options.BatchSize,
                    options.BusyPollingInterval, options.IdlePollingInterval, options.MaxPollingInterval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox background service error");
                delay = options.MaxPollingInterval;
            }

            // Sleep until a wake-up signal arrives or the fallback interval elapses, whichever
            // comes first. Fallback polling stays the correctness mechanism and is never
            // disabled — a lost signal must cost latency, never data.
            //
            // The returned keys are deliberately ignored: which partition was signalled only
            // becomes actionable when partition-filtered leasing lands in a later phase. Until
            // then any signal simply means "poll now".
            await signalCoordinator.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
