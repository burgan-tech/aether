using System;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Outbox and Inbox pattern services.
/// </summary>
public static class AetherOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Adds Outbox pattern support for the specified DbContext.
    /// The DbContext must implement IHasOutbox interface.
    /// Registers the outbox store and background processor.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type that implements IHasOutbox</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action for outbox options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherOutbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherOutboxOptions>? configure = null)
        where TDbContext : DbContext, IHasOutbox
    {
        // Validate that TDbContext implements IHasOutbox
        if (!typeof(IHasOutbox).IsAssignableFrom(typeof(TDbContext)))
        {
            throw new InvalidOperationException(
                $"DbContext {typeof(TDbContext).Name} must implement IHasOutbox to use the outbox pattern. " +
                $"Add 'public DbSet<OutboxMessage> OutboxMessages {{ get; set; }}' to your DbContext.");
        }

        // Configure options
        var options = new AetherOutboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register outbox store (replaces NullOutboxStore if registered)
        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TDbContext>>();

        // Register background processor
        services.AddHostedService<OutboxProcessor<TDbContext>>();

        return services;
    }

    /// <summary>
    /// Adds Inbox pattern support for the specified DbContext.
    /// The DbContext must implement IHasInbox interface.
    /// Registers the inbox store and cleanup service.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type that implements IHasInbox</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action for inbox options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherInbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherInboxOptions>? configure = null)
        where TDbContext : DbContext, IHasInbox
    {
        // Validate that TDbContext implements IHasInbox
        if (!typeof(IHasInbox).IsAssignableFrom(typeof(TDbContext)))
        {
            throw new InvalidOperationException(
                $"DbContext {typeof(TDbContext).Name} must implement IHasInbox to use the inbox pattern. " +
                $"Add 'public DbSet<InboxMessage> InboxMessages {{ get; set; }}' to your DbContext.");
        }

        // Configure options
        var options = new AetherInboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register inbox store (replaces NullInboxStore if registered)
        services.AddScoped<IInboxStore, EfCoreInboxStore<TDbContext>>();

        // Register cleanup service
        services.AddHostedService<InboxCleanupService<TDbContext>>();

        return services;
    }
}

