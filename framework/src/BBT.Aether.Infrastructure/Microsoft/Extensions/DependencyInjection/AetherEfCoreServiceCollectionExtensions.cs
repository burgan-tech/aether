using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Interceptors;
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
}