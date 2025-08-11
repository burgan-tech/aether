using System;
using BBT.Aether.Guids;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherInfrastructureModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAetherInfrastructure(
        this IServiceCollection services,
        Action<IServiceCollection>? configureServices = null)
    {
        configureServices?.Invoke(services);
        services.ReplaceSingleton<IGuidGenerator>(SequentialGuidGenerator.Instance);
        return services;
    }
}