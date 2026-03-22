using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Mapper;
using BBT.Aether.Mapper.AutoMapper;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherAutoMapperServiceCollectionExtensions
{
    /// <summary>
    /// Registers AutoMapper as the mapper implementation.
    /// Use this only if you hold a valid AutoMapper commercial license (required since v13+).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="autoMapperTypes">Types whose assemblies are scanned for AutoMapper profiles.</param>
    /// <param name="configure">Optional action to configure <see cref="AutoMapperOptions"/> (e.g. set the license key).</param>
    public static IServiceCollection AddAetherAutoMapperMapper(
        this IServiceCollection services,
        IEnumerable<Type> autoMapperTypes,
        Action<AutoMapperOptions>? configure = null)
    {
        var options = new AutoMapperOptions();
        configure?.Invoke(options);

        services.AddAutoMapper(cfg =>
            {
                if (options.LicenseKey is not null)
                {
                    cfg.LicenseKey = options.LicenseKey;
                }
            },
            autoMapperTypes.Select(s => s.Assembly).ToArray());

        services.AddSingleton<IObjectMapper, AutoMapperAdapter>();
        services.AddSingleton(typeof(IObjectMapper<,>), typeof(AutoMapperAdapter<,>));

        return services;
    }
}
