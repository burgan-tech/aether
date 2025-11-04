using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Background service that cleans up old processed inbox messages.
/// </summary>
public class InboxCleanupService<TDbContext>(
    IServiceProvider serviceProvider,
    ILogger<InboxCleanupService<TDbContext>> logger,
    AetherInboxOptions options)
    : BackgroundService
    where TDbContext : DbContext, IHasInbox
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("InboxCleanupService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error cleaning up inbox messages");
            }

            await Task.Delay(options.CleanupInterval, stoppingToken);
        }

        logger.LogInformation("InboxCleanupService stopped");
    }

    private async Task CleanupOldMessagesAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var cutoffDate = DateTime.UtcNow - options.RetentionPeriod;

        var oldMessages = await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Processed && m.HandledTime != null && m.HandledTime < cutoffDate)
            .Take(options.CleanupBatchSize)
            .ToListAsync(cancellationToken);

        if (oldMessages.Any())
        {
            logger.LogInformation("Cleaning up {Count} old inbox messages", oldMessages.Count);
            dbContext.InboxMessages.RemoveRange(oldMessages);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}

