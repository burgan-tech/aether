using System;
using System.Linq;
using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Dapr;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring Aether Background Job services.
/// </summary>
public static class AetherBackgroundJobServiceCollectionExtensions
{
    /// <summary>
    /// Adds Aether Background Job core infrastructure for the specified DbContext.
    /// This is scheduler-agnostic - you must also call a scheduler-specific extension like AddDaprJobScheduler().
    /// The DbContext must implement IHasEfCoreBackgroundJobs interface.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type that implements IHasEfCoreBackgroundJobs</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action for background job options</param>
    /// <returns>The service collection for method chaining</returns>
    /// <example>
    /// services.AddAetherBackgroundJob&lt;MyDbContext&gt;(options =>
    /// {
    ///     options.AddHandler&lt;MyJobHandler&gt;();
    /// })
    /// .AddDaprJobScheduler(); // Choose your scheduler
    /// </example>
    public static IServiceCollection AddAetherBackgroundJob<TDbContext>(
        this IServiceCollection services,
        Action<BackgroundJobOptions>? configure = null)
        where TDbContext : DbContext, IHasEfCoreBackgroundJobs
    {
        // Validate that TDbContext implements IHasEfCoreBackgroundJobs
        if (!typeof(IHasEfCoreBackgroundJobs).IsAssignableFrom(typeof(TDbContext)))
        {
            throw new InvalidOperationException(
                $"DbContext {typeof(TDbContext).Name} must implement IHasEfCoreBackgroundJobs to use the background job pattern. " +
                $"Add 'public DbSet<BackgroundJobInfo> BackgroundJobs {{ get; set; }}' to your DbContext.");
        }

        // Configure options
        var options = new BackgroundJobOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register core services (scheduler-agnostic)
        services.TryAddScoped<IJobStore, EfCoreJobStore<TDbContext>>();
        services.TryAddScoped<IBackgroundJobService, BackgroundJobService>();
        services.TryAddScoped<IJobDispatcher, JobDispatcher>();

        // Register handlers in DI container and create invokers (reflection only at startup)
        foreach (var handlerReg in options.Handlers)
        {
            var interfaceType = handlerReg.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBackgroundJobHandler<>));

            if (interfaceType == null)
            {
                throw new InvalidOperationException(
                    $"Handler type '{handlerReg.HandlerType.Name}' does not implement IBackgroundJobHandler<TArgs>");
            }

            // Register handler in DI
            services.TryAddScoped(interfaceType, handlerReg.HandlerType);

            // Extract TArgs type from IBackgroundJobHandler<TArgs>
            var argsType = interfaceType.GetGenericArguments()[0];

            // Create BackgroundJobInvoker<TArgs> - reflection ONLY here at startup
            var invokerType = typeof(BackgroundJobInvoker<>).MakeGenericType(argsType);
            var invoker = (IBackgroundJobInvoker)Activator.CreateInstance(invokerType)!;

            // Store invoker in options for runtime use (no reflection needed at runtime)
            options.Invokers[handlerReg.HandlerName] = invoker;
        }

        return services;
    }

    /// <summary>
    /// Adds Dapr as the job scheduler for Aether Background Jobs.
    /// Must be called after AddAetherBackgroundJob().
    /// Registers DaprJobScheduler as IJobScheduler implementation and the Dapr-specific job execution bridge.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for method chaining</returns>
    /// <example>
    /// services.AddAetherBackgroundJob&lt;MyDbContext&gt;()
    ///         .AddDaprJobScheduler();
    /// </example>
    public static IServiceCollection AddDaprJobScheduler(this IServiceCollection services)
    {
        // Register Dapr-specific scheduler
        services.TryAddScoped<IJobScheduler, DaprJobScheduler>();
        
        // Register Dapr-specific job execution bridge (non-generic, no reflection needed)
        services.TryAddScoped<IJobExecutionBridge, DaprJobExecutionBridge>();

        return services;
    }
}
