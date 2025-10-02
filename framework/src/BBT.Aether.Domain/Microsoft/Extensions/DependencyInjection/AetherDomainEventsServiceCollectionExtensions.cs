using System;
using System.Linq;
using System.Reflection;
using BBT.Aether.Domain.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering domain events services.
/// </summary>
public static class AetherDomainEventsServiceCollectionExtensions
{
    /// <summary>
    /// Adds domain events support to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies to scan for domain event handlers.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDomainEvents(this IServiceCollection services, params Assembly[] assemblies)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Register the domain event dispatcher
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // If no assemblies provided, use the calling assembly
        if (assemblies == null || assemblies.Length == 0)
        {
            assemblies = new[] { Assembly.GetCallingAssembly() };
        }

        // Register all domain event handlers
        RegisterDomainEventHandlers(services, assemblies);

        return services;
    }

    /// <summary>
    /// Adds domain events support to the service collection with automatic assembly scanning.
    /// This method will scan all loaded assemblies for domain event handlers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDomainEventsFromLoadedAssemblies(this IServiceCollection services)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToArray();

        return services.AddDomainEvents(assemblies);
    }

    private static void RegisterDomainEventHandlers(IServiceCollection services, Assembly[] assemblies)
    {
        var handlerInterface = typeof(IDomainEventHandler<>);
        
        var handlerTypes = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface && type.IsClass)
            .Where(type => type.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface))
            .ToList();

        foreach (var handlerType in handlerTypes)
        {
            var implementedInterfaces = handlerType.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface)
                .ToList();

            foreach (var implementedInterface in implementedInterfaces)
            {
                services.AddScoped(implementedInterface, handlerType);
            }
        }
    }
}
