using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using Dapr.Jobs.Extensions;
using Microsoft.AspNetCore.Builder;
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
                logger.LogError(ex, "Error processing Dapr job '{JobName}'", jobName);
                throw;
            }
        });

        return app;
    }
}