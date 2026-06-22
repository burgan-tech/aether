using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Interceptors;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherEfCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds Aether DbContext with EF Core configuration, using the supplied database provider.
    /// The supplied connection string is the one the UnitOfWork opens its single shared
    /// connection from; the configure delegate is captured to build schema-bound contexts that
    /// all enlist on that shared connection. Schema is resolved at runtime by the provider, so
    /// do not bake a schema into the model.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddAetherDbContext&lt;MyDbContext&gt;(provider, connectionString);
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherDbContext<TDbContext>(
        this IServiceCollection services,
        IAetherDatabaseProvider provider,
        string connectionString,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
    {
        services.AddSingleton<AuditInterceptor>();

        // Always include AuditInterceptor regardless of what the consumer configures.
        Action<IServiceProvider, DbContextOptionsBuilder> wrapped = (sp, b) =>
        {
            configure?.Invoke(sp, b);
            b.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        };

        // The configurator builds options bound to the UoW's shared connection.
        services.AddScoped<IAetherDbContextConfigurator<TDbContext>>(sp =>
            new AetherDbContextConfigurator<TDbContext>(connectionString, provider, wrapped, sp));

        // Keep a design-time/migrations registration of the scoped DbContext.
        services.AddDbContext<TDbContext>((sp, b) => { wrapped(sp, b); provider.ApplyConnectionString(b, connectionString); });

        services.AddAetherUnitOfWork<TDbContext>();

        return services;
    }

    /// <summary>
    /// Registers Unit of Work services for the specified DbContext.
    /// Includes ambient accessor, UoW manager, and the shared-connection DbContext provider.
    /// </summary>
    public static IServiceCollection AddAetherUnitOfWork<TDbContext>(this IServiceCollection services)
        where TDbContext : AetherDbContext<TDbContext>
    {
        // Register ambient accessor as singleton (AsyncLocal storage)
        services.TryAddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();

        // Register UoW manager as scoped
        services.TryAddScoped<IUnitOfWorkManager, UnitOfWorkManager>();

        services.AddScoped(typeof(IAetherDbContextProvider<>), typeof(AetherDbContextProvider<>));

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
        // Delegate to the overload that accepts IServiceProvider
        return services.AddAetherDbContextFactory<TDbContext, TFactory>((_, builder) => options(builder));
    }

    /// <summary>
    /// Adds IDbContextFactory.
    /// </summary>
    public static IServiceCollection AddAetherDbContextFactory<TDbContext, TFactory>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> options)
        where TDbContext : AetherDbContext<TDbContext>
        where TFactory : class, IDbContextFactory<TDbContext>
    {
        services.AddSingleton<AuditInterceptor>();

        services.AddSingleton<DbContextOptions<TDbContext>>(sp =>
        {
            var builder = new DbContextOptionsBuilder<TDbContext>();
            options.Invoke(sp, builder);
            builder.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
            return builder.Options;
        });

        services.AddScoped<IDbContextFactory<TDbContext>, TFactory>();

        return services;
    }
}