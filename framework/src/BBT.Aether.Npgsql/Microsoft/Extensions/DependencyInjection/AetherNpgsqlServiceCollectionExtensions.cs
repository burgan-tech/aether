using System;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.Persistence;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherNpgsqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Aether DbContext backed by PostgreSQL (Npgsql).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="mode">
    /// Schema switching strategy. Default is <see cref="SchemaSwitchingMode.TransactionLocal"/>
    /// (requires <c>IsTransactional = true</c>). Use <see cref="SchemaSwitchingMode.SessionSearchPath"/>
    /// for non-transactional UoWs with Npgsql's native connection pool.
    /// </param>
    /// <param name="configure">Optional additional DbContext options.</param>
    /// <example>
    /// <code>
    /// // Transactional (default):
    /// services.AddAetherNpgsql&lt;MyDbContext&gt;(connectionString);
    ///
    /// // Non-transactional with direct/session pool:
    /// services.AddAetherNpgsql&lt;MyDbContext&gt;(connectionString, SchemaSwitchingMode.SessionSearchPath);
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherNpgsql<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
    {
        services.AddAetherDbContext<TDbContext>(new NpgsqlAetherProvider(mode), connectionString, configure);

        if (typeof(IHasEfCoreOutbox).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IOutboxLeaseStore),
                typeof(NpgsqlOutboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        if (typeof(IHasEfCoreInbox).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IInboxLeaseStore),
                typeof(NpgsqlInboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        if (typeof(IHasEfCoreBackgroundJobs).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IJobArmingLeaseStore),
                typeof(EfCoreJobArmingLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        return services;
    }
}
