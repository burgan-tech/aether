using System;
using System.Linq;
using BBT.Aether.AspNetCore.MultiSchema;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring schema resolution services.
/// </summary>
public static class AetherSchemaResolutionServiceCollectionExtensions
{
    /// <summary>
    /// Adds schema resolution services to the service collection.
    /// This includes built-in strategies (Route, Header, QueryString) and the resolution middleware.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action for schema resolution options.</param>
    /// <returns>The service collection for method chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddSchemaResolution(options =>
    /// {
    ///     options.HeaderKey = "X-Runtime-Schema";
    ///     options.QueryStringKey = "schema";
    ///     options.RouteValueKey = "schema";
    ///     options.ThrowIfNotFound = true;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddSchemaResolution(
        this IServiceCollection services,
        Action<SchemaResolutionOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);

        // Built-in strategies (order matters - first registered has priority)

        // Route has highest priority, then header, then query string
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ISchemaResolutionStrategy, RouteSchemaResolutionStrategy>());
        services.TryAddEnumerable(ServiceDescriptor
            .Transient<ISchemaResolutionStrategy, QueryStringSchemaResolutionStrategy>());
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ISchemaResolutionStrategy, HeaderSchemaResolutionStrategy>());

        // Composite resolver
        services.AddTransient<ICurrentSchemaResolver, CompositeSchemaResolver>();

        // Middleware
        services.AddTransient<SchemaResolutionMiddleware>();

        return services;
    }

    public static IServiceCollection ReplaceSchemaResolver<TOld, TNew>(this IServiceCollection services)
        where TOld : ISchemaResolutionStrategy
        where TNew : class, ISchemaResolutionStrategy
    {
        var items = services
            .Where(s => s.ServiceType == typeof(ISchemaResolutionStrategy) &&
                        s.ImplementationType == typeof(TOld))
            .ToList();

        foreach (var item in items)
        {
            services.Remove(item);
        }

        services.AddTransient<ISchemaResolutionStrategy, TNew>();

        return services;
    }

    public static IServiceCollection RemoveSchemaResolver<T>(this IServiceCollection services)
        where T : ISchemaResolutionStrategy
    {
        var items = services
            .Where(s => s.ServiceType == typeof(ISchemaResolutionStrategy) &&
                        s.ImplementationType == typeof(T))
            .ToList();

        foreach (var item in items)
        {
            services.Remove(item);
        }

        return services;
    }
}