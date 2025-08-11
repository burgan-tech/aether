using System;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherApplicationModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAetherApplication(
        this IServiceCollection services,
        Action<IServiceCollection>? configureServices = null)
    {
        configureServices?.Invoke(services);
        return services;
    }
}