using System;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddAetherOutbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherOutboxOptions>? configure = null,
        bool withHostedService = false)
        where TDbContext : DbContext, IHasEfCoreOutbox
    {
        var options = new AetherOutboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TDbContext>>();

        // Null fallback — provider (Npgsql/SqlServer) overrides with AddScoped
        services.TryAddScoped<IOutboxLeaseStore, NullOutboxLeaseStore>();

        // Null fallback partition lease store — overridden by NpgsqlPartitionLeaseStore when
        // AddAetherNpgsql<TDbContext> detects IHasEfCorePartitionLeases.
        services.TryAddScoped<IPartitionLeaseStore, NullPartitionLeaseStore>();

        // WorkerIdentity singleton — guard against double registration
        services.TryAddSingleton<WorkerIdentity>();

        services.AddSingleton<IOutboxProcessor, OutboxProcessor<TDbContext>>();

        if (withHostedService)
            services.AddHostedService<OutboxBackgroundService>();

        return services;
    }

    public static IServiceCollection AddAetherInbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherInboxOptions>? configure = null,
        bool withHostedService = false)
        where TDbContext : DbContext, IHasEfCoreInbox
    {
        var options = new AetherInboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddScoped<IInboxStore, EfCoreInboxStore<TDbContext>>();

        services.TryAddScoped<IInboxLeaseStore, NullInboxLeaseStore>();

        // Null fallback partition lease store — overridden by NpgsqlPartitionLeaseStore when
        // AddAetherNpgsql<TDbContext> detects IHasEfCorePartitionLeases.
        services.TryAddScoped<IPartitionLeaseStore, NullPartitionLeaseStore>();

        services.TryAddSingleton<WorkerIdentity>();

        services.AddSingleton<IInboxProcessor, InboxProcessor<TDbContext>>();

        if (withHostedService)
            services.AddHostedService<InboxBackgroundService>();

        return services;
    }
}

