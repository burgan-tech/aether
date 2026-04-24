using System;
using BBT.Aether.Application.Pagination;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherApplicationModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAetherApplication(
        this IServiceCollection services,
        Action<IServiceCollection>? configureServices = null)
    {
        configureServices?.Invoke(services);

        // Pagination link generator lives in the Application layer (transport-agnostic).
        // The transport adapter (e.g. AspNetCore) replaces IPaginationContext with its own
        // implementation; non-HTTP hosts stay on NullPaginationContext and produce route-only links.
        services.TryAddSingleton<IPaginationContext>(NullPaginationContext.Instance);
        services.TryAddScoped<IPaginationLinkGenerator, PaginationLinkGenerator>();

        return services;
    }
}