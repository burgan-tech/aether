using System;
using BBT.Aether.Domain.Services;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherDomainModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAetherDomain(
        this IServiceCollection services,
        Action<IServiceCollection>? configureServices = null)
    {
        configureServices?.Invoke(services);
        services.AddScoped<IMultiLingualEntityManager, MultiLingualEntityManager>();
        return services;
    }
}