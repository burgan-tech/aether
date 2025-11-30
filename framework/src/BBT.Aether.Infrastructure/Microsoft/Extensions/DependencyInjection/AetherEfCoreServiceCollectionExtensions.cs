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
    /// Adds Aether DbContext with EF Core configuration.
    /// Use this overload if you don't need access to IServiceProvider in your configuration.
    /// </summary>
    public static IServiceCollection AddAetherDbContext<TDbContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> options)
        where TDbContext : AetherDbContext<TDbContext>
    {
        // Delegate to the overload that accepts IServiceProvider
        return services.AddAetherDbContext<TDbContext>((_, builder) => options(builder));
    }

    /// <summary>
    /// Adds Aether DbContext with EF Core configuration.
    /// Use this overload when you need access to IServiceProvider (e.g., to add interceptors from DI).
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddAetherDbContext&lt;MyDbContext&gt;((sp, options) =>
    /// {
    ///     options.UseNpgsql(connectionString);
    ///     options.AddInterceptors(sp.GetRequiredService&lt;NpgsqlSchemaConnectionInterceptor&gt;());
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherDbContext<TDbContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> options)
        where TDbContext : AetherDbContext<TDbContext>
    {
        services.AddSingleton<AuditInterceptor>();

        services.AddDbContext<TDbContext>((sp, dbContextOptions) =>
        {
            options.Invoke(sp, dbContextOptions);
            dbContextOptions.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>()
            );
        });

        // Register Unit of Work services
        services.AddAetherUnitOfWork<TDbContext>();

        return services;
    }

    /// <summary>
    /// Registers Unit of Work services for the specified DbContext.
    /// Includes ambient accessor, UoW manager, EF Core transaction source, and domain event sink.
    /// </summary>
    public static IServiceCollection AddAetherUnitOfWork<TDbContext>(this IServiceCollection services)
        where TDbContext : AetherDbContext<TDbContext>
    {
        // Register ambient accessor as singleton (AsyncLocal storage)
        services.TryAddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();

        // Register UoW manager as scoped
        services.TryAddScoped<IUnitOfWorkManager, UnitOfWorkManager>();

        // Register domain event sink to bridge DbContext and UoW
        services.TryAddScoped<IDomainEventSink, UnitOfWorkDomainEventSink>();

        // Register EF Core transaction source for this DbContext
        services.AddScoped<ILocalTransactionSource, EfCoreTransactionSource<TDbContext>>();
        
        services.AddScoped(typeof(IDbContextProvider<>), typeof(DbContextProvider<>));

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