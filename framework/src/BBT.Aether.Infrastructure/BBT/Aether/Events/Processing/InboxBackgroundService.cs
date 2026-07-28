using System;
using System.Threading;
using System.Threading.Tasks;
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
        var delay = options.IdlePollingInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(stoppingToken);
                delay = AdaptivePolling.NextDelay(
                    delay, processed, options.ProcessingBatchSize,
                    options.BusyPollingInterval, options.IdlePollingInterval, options.MaxPollingInterval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox background service error");
                delay = options.ErrorPollingInterval;
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
