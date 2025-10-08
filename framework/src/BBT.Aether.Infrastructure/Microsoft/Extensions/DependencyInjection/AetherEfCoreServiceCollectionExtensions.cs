using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Interceptors;
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Events.Distributed;
using BBT.Aether.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherEfCoreServiceCollectionExtensions
{
    public static IServiceCollection AddAetherDbContext<TDbContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> options)
        where TDbContext : DbContext
    {
        services.AddSingleton<AuditInterceptor>();

        // Register domain event dispatcher if not already registered
        services.TryAddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Register distributed event publisher if not already registered
        services.TryAddScoped<IDistributedDomainEventPublisher, NullDistributedDomainEventPublisher>();
        
        // Register the unified event context
        services.TryAddScoped<IEventContext, EventContext>();

        services.AddDbContext<TDbContext>((sp, dbContextOptions) =>
        {
            options?.Invoke(dbContextOptions);
            dbContextOptions.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>()
            );
        });

        services.AddScoped<ITransactionService, EfCoreTransactionService<TDbContext>>();
        return services;
    }

    /// <summary>
    /// Adds IDbContextFactory with domain events support.
    /// </summary>
    public static IServiceCollection AddAetherDbContextFactory<TDbContext, TFactory>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> options)
        where TDbContext : AetherDbContext<TDbContext>
        where TFactory : class, IDbContextFactory<TDbContext>
    {
        services.AddSingleton<AuditInterceptor>();
        
        // Register domain event dispatcher if not already registered
        services.TryAddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Register distributed event publisher if not already registered
        services.TryAddScoped<IDistributedDomainEventPublisher, NullDistributedDomainEventPublisher>();
        
        // Register the unified event context
        services.TryAddScoped<IEventContext, EventContext>();
        
        // Configure DbContextOptions
        services.AddDbContextOptions<TDbContext>(options);
        
        // Register the factory
        services.AddScoped<IDbContextFactory<TDbContext>, TFactory>();
        services.AddScoped<TFactory>();

        return services;
    }

    private static IServiceCollection AddDbContextOptions<TDbContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> options)
        where TDbContext : DbContext
    {
        services.AddSingleton<DbContextOptions<TDbContext>>(sp =>
        {
            var builder = new DbContextOptionsBuilder<TDbContext>();
            options?.Invoke(builder);
            return builder.Options;
        });

        return services;
    }

    /// <summary>
    /// Adds a custom distributed domain event publisher implementation.
    /// </summary>
    /// <typeparam name="TPublisher">The type of the distributed event publisher.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDistributedDomainEventPublisher<TPublisher>(
        this IServiceCollection services)
        where TPublisher : class, IDistributedDomainEventPublisher
    {
        services.Replace(ServiceDescriptor.Scoped<IDistributedDomainEventPublisher, TPublisher>());
        return services;
    }
}