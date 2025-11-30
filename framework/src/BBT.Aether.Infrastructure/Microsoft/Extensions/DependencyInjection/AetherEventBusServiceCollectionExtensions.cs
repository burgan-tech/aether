using System;
using BBT.Aether.Events;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherEventBusServiceCollectionExtensions
{
    /// <summary>
    /// Adds Aether Event Bus with Dapr support and automatic handler registration from all loaded assemblies.
    /// This method registers all core event bus services, discovers and registers handlers,
    /// builds event subscription descriptors, and configures the DaprEventBus.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action for event bus options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddAetherEventBus(this IServiceCollection services, Action<AetherEventBusOptions>? configure = null)
    {
        // Core event bus registrations
        services.TryAddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<ITopicNameStrategy, DefaultTopicNameStrategy>();
        services.AddScoped<IOutboxStore, NullOutboxStore>();
        services.AddScoped<IInboxStore, NullInboxStore>();

        // Configure event bus options
        var options = new AetherEventBusOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Get environment name for topic prefixing
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        // Register handlers and build invokers immediately
        var invokers = EventHandlerAutoDiscovery.RegisterHandlersAndBuildInvokers(
            services,
            options,
            environmentName);

        // Create and register invoker registry
        var registry = new DistributedEventInvokerRegistry(invokers);
        services.AddSingleton<IDistributedEventInvokerRegistry>(registry);

        // Register DaprEventBus as scoped (requires IOutboxStore which is scoped)
        services.AddScoped<IDistributedEventBus, DaprEventBus>();

        return services;
    }
}