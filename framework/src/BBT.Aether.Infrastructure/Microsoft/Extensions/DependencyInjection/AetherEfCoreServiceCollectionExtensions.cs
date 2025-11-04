using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Interceptors;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
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
    /// Adds domain event dispatching support for the specified DbContext.
    /// Enables aggregates to raise distributed events via AddDistributedEvent().
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action for domain event options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherDomainEvents<TDbContext>(
        this IServiceCollection services,
        Action<AetherDomainEventOptions>? configure = null)
        where TDbContext : DbContext
    {
        // Configure options
        var options = new AetherDomainEventOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register domain event dispatcher
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        return services;
    }

    /// <summary>
    /// Adds IDbContextFactory.
    /// </summary>
    public static IServiceCollection AddAetherDbContextFactory<TDbContext, TFactory>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> options)
        where TDbContext : AetherDbContext<TDbContext>
        where TFactory : class, IDbContextFactory<TDbContext>
    {
        services.AddSingleton<AuditInterceptor>();
        
        // Configure DbContextOptions
        services.AddDbContextOptions<TDbContext>(options);
        
        // Register the factory
        services.AddScoped<IDbContextFactory<TDbContext>, TFactory>();

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
}