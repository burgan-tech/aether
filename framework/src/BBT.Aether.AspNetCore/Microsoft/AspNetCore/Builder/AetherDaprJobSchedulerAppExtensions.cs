using System;
using System.Threading;
using BBT.Aether.BackgroundJob;
using Dapr.Jobs.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for configuring Dapr job scheduler in ASP.NET Core applications.
/// </summary>
public static class AetherDaprJobSchedulerAppExtensions
{
    /// <summary>
    /// Maps Dapr scheduled job handler endpoint to the application.
    /// This endpoint receives job trigger requests from Dapr and routes them to the appropriate handlers
    /// via the DaprJobExecutionBridge (no reflection needed at runtime).
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for method chaining</returns>
    public static WebApplication UseDaprScheduledJobHandler(this WebApplication app)
    {
        app.MapDaprScheduledJobHandler(async (string jobName, ReadOnlyMemory<byte> jobPayload,
            CancellationToken cancellationToken) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var serviceProvider = scope.ServiceProvider;
            var logger = serviceProvider.GetRequiredService<ILogger<WebApplication>>();

            try
            {
                var executionBridge = serviceProvider.GetRequiredService<IJobExecutionBridge>();
                await executionBridge.ExecuteAsync(jobName, jobPayload, cancellationToken);
            }
            catch (Exception ex)
            {
                // Error semantics contract: handler failures do NOT reach here. The dispatcher records the
                // outcome (Completed / Retrying / Failed for one-shot, back-to-Scheduled for recurring) and
                // returns normally, so Dapr sees a 200 and does NOT retry — framework retry owns handler retries.
                // Only infrastructure faults (the bridge or dispatcher itself threw) propagate here; rethrowing
                // makes Dapr observe a non-200 and trigger its own delivery retry. Do not swallow.
                logger.LogError(ex, "Error processing Dapr job '{JobName}'", jobName);
                throw;
            }
        });

        return app;
    }
}