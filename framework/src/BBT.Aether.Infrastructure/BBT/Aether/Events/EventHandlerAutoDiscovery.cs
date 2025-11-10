using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Events;

/// <summary>
/// Discovers and registers event handlers at startup.
/// Builds precompiled invokers for reflection-free event dispatching.
/// </summary>
public static class EventHandlerAutoDiscovery
{
    private class HandlerMetadata
    {
        public Type HandlerType { get; init; } = null!;
        public Type HandlerInterface { get; init; } = null!;
        public Type EventType { get; init; } = null!;
    }

    /// <summary>
    /// Discovers all IEventHandler&lt;T&gt; implementations, registers them in DI,
    /// and returns a list of precompiled invokers.
    /// </summary>
    /// <param name="services">The service collection to register handlers in</param>
    /// <param name="options">The event bus options containing configuration</param>
    /// <param name="environmentName">The environment name (e.g., "dev", "prod")</param>
    /// <returns>A list of event invokers for building the registry</returns>
    public static List<IDistributedEventInvoker> RegisterHandlersAndBuildInvokers(
        IServiceCollection services,
        AetherEventBusOptions options,
        string? environmentName)
    {
        var invokers = new List<IDistributedEventInvoker>();
        var handlerMetadata = DiscoverHandlerMetadata();

        foreach (var metadata in handlerMetadata)
        {
            // Register handler in DI as scoped
            services.AddScoped(metadata.HandlerInterface, metadata.HandlerType);

            // Get EventMeta for this event type to build invoker
            // We use reflection here once at startup to get the EventMeta<T> type
            var eventMetaType = typeof(EventMeta<>).MakeGenericType(metadata.EventType);
            
            // Extract metadata from EventMeta<T> static fields
            var nameField = eventMetaType.GetField(nameof(EventMeta<object>.Name), BindingFlags.Public | BindingFlags.Static);
            var versionField = eventMetaType.GetField(nameof(EventMeta<object>.Version), BindingFlags.Public | BindingFlags.Static);
            var pubSubField = eventMetaType.GetField(nameof(EventMeta<object>.PubSub), BindingFlags.Public | BindingFlags.Static);
            var topicField = eventMetaType.GetField(nameof(EventMeta<object>.Topic), BindingFlags.Public | BindingFlags.Static);
            
            var eventName = (string)nameField!.GetValue(null)!;
            var version = (int)versionField!.GetValue(null)!;
            var pubSubName = (string?)pubSubField!.GetValue(null);
            var topicOverride = (string?)topicField!.GetValue(null);
            
            // Resolve PubSubName: use from event metadata or fall back to options default
            var resolvedPubSubName = pubSubName ?? options.PubSubName;
            
            // Build topic name: use override if specified, otherwise generate from name and version
            string topicName;
            if (!string.IsNullOrWhiteSpace(topicOverride))
            {
                topicName = topicOverride;
            }
            else
            {
                // Generate topic: EventName.vVersion format
                topicName = $"{eventName}.v{version}";
            }
            
            // Apply environment prefix if enabled
            if (options.PrefixEnvironmentToTopic && !string.IsNullOrWhiteSpace(environmentName))
            {
                topicName = $"{environmentName.ToLowerInvariant()}.{topicName}";
            }

            // Create invoker using reflection (once at startup)
            var invokerType = typeof(DistributedEventInvoker<>).MakeGenericType(metadata.EventType);
            var invoker = (IDistributedEventInvoker)Activator.CreateInstance(
                invokerType,
                topicName,
                resolvedPubSubName)!;
            
            invokers.Add(invoker);
        }

        return invokers;
    }

    private static List<HandlerMetadata> DiscoverHandlerMetadata()
    {
        var metadata = new List<HandlerMetadata>();
        
        // Get all loaded assemblies from AppDomain
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        
        // Find all handler types implementing IEventHandler<>
        var handlerTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>))
                .Select(i => new { Handler = t, Interface = i }));

        foreach (var handlerInfo in handlerTypes)
        {
            var handlerType = handlerInfo.Handler;
            var handlerInterface = handlerInfo.Interface;
            var eventType = handlerInterface.GetGenericArguments()[0];

            metadata.Add(new HandlerMetadata
            {
                HandlerType = handlerType,
                HandlerInterface = handlerInterface,
                EventType = eventType
            });
        }

        return metadata;
    }
}
