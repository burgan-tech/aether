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
    ILogger<InboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spread the first poll of replicas that booted together, so a rolling deployment does not
        // leave the whole fleet polling on the same tick.
        try
        {
            await Task.Delay(PollingDelay.StartupOffset(options.IdlePollingInterval), stoppingToken)
                .ConfigureAwait(false);
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

            await Task.Delay(PollingDelay.Jitter(delay), stoppingToken).ConfigureAwait(false);
        }
    }
}
