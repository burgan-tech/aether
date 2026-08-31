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

        // WorkerIdentity singleton — guard against double registration
        services.TryAddSingleton<WorkerIdentity>();

        services.AddSingleton<IOutboxProcessor, OutboxProcessor<TDbContext>>();

        // Scoped, matching the scoped EfCoreOutboxStore + scoped IUnitOfWorkManager it consumes
        // (singleton would be a captive dependency). Per-UoW dedupe survives across scoped instances
        // because the registration table is static.
        services.TryAddScoped<OutboxWakeupCoordinator>();
        if (options.WakeupSignalEnabled)
        {
            services.TryAddSingleton<IOutboxWakeupNotifier, DaprOutboxWakeupNotifier>();
        }
        services.TryAddSingleton<
            BBT.Aether.Polling.IPollingWakeSignal<IOutboxProcessor>,
            BBT.Aether.Polling.PollingWakeSignal<IOutboxProcessor>>();

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

        services.TryAddSingleton<WorkerIdentity>();

        services.AddSingleton<IInboxProcessor, InboxProcessor<TDbContext>>();

        services.TryAddSingleton<
            BBT.Aether.Polling.IPollingWakeSignal<IInboxProcessor>,
            BBT.Aether.Polling.PollingWakeSignal<IInboxProcessor>>();

        if (withHostedService)
            services.AddHostedService<InboxBackgroundService>();

        return services;
    }
}

