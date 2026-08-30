using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public sealed class InboxBackgroundService(
    IInboxProcessor processor,
    AetherInboxOptions options,
    ILogger<InboxBackgroundService> logger,
    BBT.Aether.Polling.IPollingWakeSignal<IInboxProcessor>? wakeSignal = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spread the first poll of replicas that booted together, so a rolling deployment does not
        // leave the whole fleet polling on the same tick.
        try
        {
            // Startup offset: also wake-aware, so a nudge that lands during a rolling restart advances the
            // first poll instead of waiting the offset out.
            if (wakeSignal is null)
                await Task.Delay(PollingDelay.StartupOffset(options.IdlePollingInterval), stoppingToken).ConfigureAwait(false);
            else
                await wakeSignal.WaitAsync(PollingDelay.StartupOffset(options.IdlePollingInterval), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var delay = options.IdlePollingInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(stoppingToken);
                delay = processed > 0
                    ? PollingDelay.OnProcessed(options.BusyPollingInterval)
                    : PollingDelay.OnEmpty(delay, options.MaxPollingInterval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox background service error");
                // One step back, not straight to the cap: a transient fault must not stall every
                // replica for a full maximum interval.
                delay = PollingDelay.OnError(delay, options.IdlePollingInterval, options.MaxPollingInterval);
            }

            // A wake signal cuts the interval short; timeout keeps polling as the safety net.
            if (wakeSignal is null)
                await Task.Delay(PollingDelay.Jitter(delay), stoppingToken).ConfigureAwait(false);
            else
                await wakeSignal.WaitAsync(PollingDelay.Jitter(delay), stoppingToken).ConfigureAwait(false);
        }
    }
}
