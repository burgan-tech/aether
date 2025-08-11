using System;
using System.Linq;
using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Dapr;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherBackgroundJobServiceCollectionExtensions
{
    public static IServiceCollection AddDaprJobScheduler<TJobInfo, TRepository>(this IServiceCollection services, Action<DaprJobSchedulerOptions> configureOptions)
           where TJobInfo : BackgroundJobInfo
            where TRepository : class, IRepository<TJobInfo>

    {
        services.AddScoped<IBackgroundJobService, DaprBackgroundJobService<TJobInfo>>();
        services.AddScoped<IRepository<TJobInfo>, TRepository>();

        services.Configure(configureOptions);

        services.AddSingleton<DaprJobSchedulerOptions>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DaprJobSchedulerOptions>>().Value;
            return options;
        });

        var options = new DaprJobSchedulerOptions();
        configureOptions(options);

        foreach (var handlerEntry in options.Handlers.JobHandlers)
        {
            var interfaceType = handlerEntry.JobHandler.GetInterfaces()
                       .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBackgroundJobHandler<>));

            if (interfaceType != null)
            {
                services.AddScoped(interfaceType, handlerEntry.JobHandler);
            }
        }
        
        services.AddScoped(typeof(IJobExecute<>), typeof(DaprSchedulerJobAdapter<>));

        return services;
    }
}