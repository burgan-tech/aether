using System;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Outbox and Inbox pattern services.
/// </summary>
public static class AetherOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Adds Outbox pattern support for the specified DbContext.
    /// The DbContext must implement IHasEfCoreOutbox interface.
    /// Registers the outbox store and background processor.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type that implements IHasEfCoreOutbox</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action for outbox options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherOutbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherOutboxOptions>? configure = null)
        where TDbContext : DbContext, IHasEfCoreOutbox
    {
        // Validate that TDbContext implements IHasEfCoreOutbox
        if (!typeof(IHasEfCoreOutbox).IsAssignableFrom(typeof(TDbContext)))
        {
            throw new InvalidOperationException(
                $"DbContext {typeof(TDbContext).Name} must implement IHasEfCoreOutbox to use the outbox pattern. " +
                $"Add 'public DbSet<OutboxMessage> OutboxMessages {{ get; set; }}' to your DbContext.");
        }

        // Configure options
        var options = new AetherOutboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register outbox store (replaces NullOutboxStore if registered)
        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TDbContext>>();

        // Register outbox processor
        services.AddSingleton<IOutboxProcessor, OutboxProcessor<TDbContext>>();
        
        // Register background processor
        // services.AddHostedService<OutboxProcessor<TDbContext>>();

        return services;
    }

    /// <summary>
    /// Adds Inbox pattern support for the specified DbContext.
    /// The DbContext must implement IHasEfCoreInbox interface.
    /// Registers the inbox store and processor service.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type that implements IHasEfCoreInbox</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action for inbox options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherInbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherInboxOptions>? configure = null)
        where TDbContext : DbContext, IHasEfCoreInbox
    {
        // Validate that TDbContext implements IHasEfCoreInbox
        if (!typeof(IHasEfCoreInbox).IsAssignableFrom(typeof(TDbContext)))
        {
            throw new InvalidOperationException(
                $"DbContext {typeof(TDbContext).Name} must implement IHasEfCoreInbox to use the inbox pattern. " +
                $"Add 'public DbSet<InboxMessage> InboxMessages {{ get; set; }}' to your DbContext.");
        }

        // Configure options
        var options = new AetherInboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register inbox store (replaces NullInboxStore if registered)
        services.AddScoped<IInboxStore, EfCoreInboxStore<TDbContext>>();

        // Register inbox processor
        services.AddSingleton<IInboxProcessor, InboxProcessor<TDbContext>>();

        // Note: To run the processor as a background service, add a hosted service wrapper:
        // services.AddHostedService<InboxBackgroundService>();

        return services;
    }
}

