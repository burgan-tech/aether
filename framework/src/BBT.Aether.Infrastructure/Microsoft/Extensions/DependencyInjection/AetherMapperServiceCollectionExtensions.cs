using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Mapper;
using BBT.Aether.Mapper.AutoMapper;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherMapperServiceCollectionExtensions
{
    public static IServiceCollection AddAetherAutoMapperMapper(
        this IServiceCollection services,
        List<Type> autoMapperTypes)
    {
        services.AddAutoMapper(_ =>
            {
            },
            autoMapperTypes.Select(s => s.Assembly).ToArray()
        );

        services.AddSingleton<IObjectMapper, AutoMapperAdapter>();
        services.AddSingleton(typeof(IObjectMapper<,>), typeof(AutoMapperAdapter<,>));

        return services;
    }
}