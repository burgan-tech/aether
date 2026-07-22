using System;
using BBT.Aether.BackgroundJob;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// Registers an Aether DbContext backed by PostgreSQL (Npgsql). Schema targeting always uses
    /// <see cref="SchemaSwitchingMode.QualifiedNames"/> (fully-qualified <c>"schema"."table"</c>
    /// SQL), which is safe on any pooled connection and lets non-transactional units of work
    /// leave connection management entirely to EF Core.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="mode">
    /// Schema switching strategy. <see cref="SchemaSwitchingMode.QualifiedNames"/> is the only
    /// supported value; the parameter is kept for signature compatibility.
    /// </param>
    /// <param name="configure">Optional additional DbContext options.</param>
    /// <example>
    /// <code>
    /// services.AddAetherNpgsql&lt;MyDbContext&gt;(connectionString);
    /// </code>
    /// </example>
    public static IServiceCollection AddAetherNpgsql<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        SchemaSwitchingMode mode = SchemaSwitchingMode.QualifiedNames,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
    {
        services.AddAetherDbContext<TDbContext>(new NpgsqlAetherProvider(), connectionString, configure);

        if (typeof(IHasEfCoreOutbox).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IOutboxLeaseStore),
                typeof(NpgsqlOutboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        if (typeof(IHasEfCoreInbox).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IInboxLeaseStore),
                typeof(NpgsqlInboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        if (typeof(IHasEfCoreBackgroundJobs).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IJobArmingLeaseStore),
                typeof(NpgsqlJobArmingLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        return services;
    }
}
