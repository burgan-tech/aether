using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Events;

public static class EventHandlerAutoDiscovery
{
    private class HandlerMetadata
    {
        public Type HandlerType { get; init; } = null!;
        public Type HandlerInterface { get; init; } = null!;
        public Type EventType { get; init; } = null!;
        public List<EventSubscriptionAttribute> SubscriptionAttributes { get; init; } = new();
    }

    /// <summary>
    /// Registers all IDistributedEventHandler implementations from all loaded assemblies in DI
    /// and returns event subscription descriptors for all discovered handlers.
    /// </summary>
    /// <param name="services">The service collection to register handlers in</param>
    /// <param name="options">The event bus options containing configuration</param>
    /// <param name="environmentName">The environment name (e.g., "dev", "prod")</param>
    /// <returns>A list of event subscription descriptors</returns>
    public static List<EventSubscriptionDescriptor> RegisterHandlersAndBuildDescriptors(
        IServiceCollection services,
        AetherEventBusOptions options,
        string? environmentName)
    {
        var descriptors = new List<EventSubscriptionDescriptor>();
        var handlerMetadata = DiscoverHandlerMetadata();

        foreach (var metadata in handlerMetadata)
        {
            // Register handler in DI
            services.AddScoped(metadata.HandlerInterface, metadata.HandlerType);

            // Build descriptors for each subscription
            foreach (var attr in metadata.SubscriptionAttributes)
            {
                // Generate topic name: EventName.vVersion format
                var topicName = $"{attr.EventName}.v{attr.Version}";
                
                // Apply environment prefix if enabled
                if (options.PrefixEnvironmentToTopic && !string.IsNullOrWhiteSpace(environmentName))
                {
                    topicName = $"{environmentName.ToLowerInvariant()}.{topicName}";
                }

                // Resolve PubSubName from EventSubscriptionAttribute or use default from options
                var pubSubName = !string.IsNullOrWhiteSpace(attr.PubSubName) 
                    ? attr.PubSubName 
                    : options.PubSubName;
                
                descriptors.Add(new EventSubscriptionDescriptor(
                    topicName,
                    metadata.EventType,
                    pubSubName));
            }
        }

        return descriptors;
    }

    private static List<HandlerMetadata> DiscoverHandlerMetadata()
    {
        var metadata = new List<HandlerMetadata>();
        
        // Get all loaded assemblies from AppDomain
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        
        // Find all handler types implementing IDistributedEventHandler<>
        var handlerTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDistributedEventHandler<>))
                .Select(i => new { Handler = t, Interface = i }));

        foreach (var handlerInfo in handlerTypes)
        {
            var handlerType = handlerInfo.Handler;
            var handlerInterface = handlerInfo.Interface;
            var eventType = handlerInterface.GetGenericArguments()[0];
            
            var subscriptionAttributes = handlerType
                .GetCustomAttributes<EventSubscriptionAttribute>(inherit: false)
                .ToList();

            metadata.Add(new HandlerMetadata
            {
                HandlerType = handlerType,
                HandlerInterface = handlerInterface,
                EventType = eventType,
                SubscriptionAttributes = subscriptionAttributes
            });
        }

        return metadata;
    }
}