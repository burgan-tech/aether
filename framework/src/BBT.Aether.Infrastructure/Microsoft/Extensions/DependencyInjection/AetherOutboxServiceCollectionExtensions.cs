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

        // Scoped: one collector per unit of work, so coalescing is per transaction.
        // Registered unconditionally rather than behind SignalEnabled — the collector is
        // already inert when signalling is off, and leaving it unregistered makes DI fall
        // back to EfCoreOutboxStore's legacy constructor, silently dropping the schema
        // override as well as signalling.
        services.TryAddScoped<IOutboxSignalCollector, OutboxSignalCollector>();

        // Null fallback — a broker-backed publisher is registered by the hosting application
        // via AddAetherOutboxDaprSignalling().
        services.TryAddSingleton<IOutboxWakeupPublisher, NullOutboxWakeupPublisher>();

        // Singleton: the dispatcher loop and the subscription endpoint share one instance.
        services.TryAddSingleton<IOutboxSignalCoordinator, OutboxSignalCoordinator>();

        if (withHostedService)
            services.AddHostedService<OutboxBackgroundService>();

        return services;
    }

    /// <summary>
    /// Registers the Dapr-backed wake-up signal publisher, replacing the no-op default.
    /// Call from an application that already has a <see cref="Dapr.Client.DaprClient"/> registered.
    /// </summary>
    public static IServiceCollection AddAetherOutboxDaprSignalling(this IServiceCollection services)
    {
        services.RemoveAll<IOutboxWakeupPublisher>();
        services.AddSingleton<IOutboxWakeupPublisher, DaprOutboxWakeupPublisher>();
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

        if (withHostedService)
            services.AddHostedService<InboxBackgroundService>();

        return services;
    }
}

