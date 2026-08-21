using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Processing;

/// <summary>
/// Hosted service that drives <see cref="BackgroundJobArmingProcessor"/> on a timer. Each tick runs one
/// arming pass; exceptions per tick are caught and logged so a transient failure never tears down the
/// loop. The delay between ticks is <see cref="BackgroundJobOptions.ArmingInterval"/>, jittered.
/// Registered by the DI wiring (see AddAetherBackgroundJob); not auto-registered here.
/// <para>
/// The interval is fixed — there is no adaptive backoff here, because an unarmed job must be picked up
/// within a bounded time regardless of how quiet the system is. That makes jitter the only thing
/// keeping replicas apart: without it, pods started together by a rolling deployment run every pass in
/// lockstep, turning each tick into a burst of simultaneous claim queries over the same rows. A random
/// startup offset spreads the first pass as well.
/// </para>
/// </summary>
public class BackgroundJobArmingHostedService(
    BackgroundJobArmingProcessor processor,
    BackgroundJobOptions options,
    ILogger<BackgroundJobArmingHostedService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Background-job arming poller started (interval {Interval}, schema {Schema}).",
            options.ArmingInterval, options.Schema);

        try
        {
            await Task.Delay(PollingDelay.StartupOffset(options.ArmingInterval), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error running background-job arming pass");
            }

            try
            {
                await Task.Delay(PollingDelay.Jitter(options.ArmingInterval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Background-job arming poller stopped.");
    }
}
