using System;
using System.Reflection;
using BBT.Aether;
using BBT.Aether.DependencyInjection;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Guids;
using BBT.Aether.Logging;
using BBT.Aether.Tracing;
using BBT.Aether.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherCoreModuleServiceCollectionExtensions
{
    static internal void AddCoreServices(this IServiceCollection services)
    {
        services.AddOptions();
        services.AddLogging();
        services.AddLocalization();
    }

    public static IServiceCollection AddAetherCore(
        this IServiceCollection services,
        Action<ApplicationCreationOptions>? optionsAction)
    {
        var options = new ApplicationCreationOptions();
        optionsAction?.Invoke(options);

        AddCoreServices(services);

        if (!services.IsAdded<IConfiguration>())
        {
            services.ReplaceConfiguration(
                ConfigurationHelper.BuildConfiguration(
                    options.Configuration
                )
            );
        }

        RegisterApplicationInfo(services, options);

        services.AddSingleton<ICorrelationIdProvider, DefaultCorrelationIdProvider>();
        services.AddSingleton<IGuidGenerator>(SimpleGuidGenerator.Instance);
        services.AddSingleton<ICurrentUserAccessor>(AsyncLocalCurrentUserAccessor.Instance);
        services.AddTransient<ICurrentUser, CurrentUser>();
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        services.TryAddSingleton<IInitLoggerFactory>(new DefaultInitLoggerFactory());
        services.AddTransient<IExceptionToErrorInfoConverter, DefaultExceptionToErrorInfoConverter>();

        return services;
    }

    private static void RegisterApplicationInfo(IServiceCollection services, ApplicationCreationOptions options)
    {
        var applicationInfo = new ApplicationInfoAccessor(
            GetApplicationName(services, options),
            Environment.GetEnvironmentVariable("HOSTNAME") ?? Guid.NewGuid().ToString()
        );
        services.AddSingleton<IApplicationInfoAccessor>(applicationInfo);
    }

    private static string? GetApplicationName(IServiceCollection services, ApplicationCreationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            return options.ApplicationName!;
        }

        var configuration = services.GetConfigurationOrNull();
        if (configuration != null)
        {
            var appNameConfig = configuration["ApplicationName"];
            if (!string.IsNullOrWhiteSpace(appNameConfig))
            {
                return appNameConfig!;
            }
        }

        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly != null)
        {
            return entryAssembly.GetName().Name;
        }

        return null;
    }
}