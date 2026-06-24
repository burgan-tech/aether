using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.BackgroundJob.Processing;

/// <summary>
/// Hosted service that drives <see cref="BackgroundJobArmingProcessor"/> on a timer. Each tick runs one
/// arming pass; exceptions per tick are caught and logged so a transient failure never tears down the
/// loop. The delay between ticks is <see cref="BackgroundJobOptions.ArmingInterval"/>. Registered by the
/// DI wiring (see AddAetherBackgroundJob); not auto-registered here.
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
                await Task.Delay(options.ArmingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Background-job arming poller stopped.");
    }
}
