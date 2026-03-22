using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Mapper;
using BBT.Aether.Mapper.Mapperly;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherMapperlyServiceCollectionExtensions
{
    /// <summary>
    /// Registers Mapperly as the default mapper implementation.
    /// Derives assemblies from the provided <paramref name="mapperTypes"/>, scans them for all
    /// concrete <see cref="IMapperlyMapper{TSource,TDestination}"/> and
    /// <see cref="IReverseMapperlyMapper{TSource,TDestination}"/> implementations, and registers
    /// them as singletons. The non-generic <see cref="IObjectMapper"/> is fulfilled by
    /// <see cref="MapperlyAdapter"/> which dispatches to the typed mappers via DI and invokes
    /// the <c>BeforeMap</c> / <c>AfterMap</c> lifecycle hooks.
    /// </summary>
    /// <remarks>
    /// Implement mappers by extending <see cref="MapperBase{TSource,TDestination}"/> (one-way) or
    /// <see cref="TwoWayMapperBase{TSource,TDestination}"/> (bidirectional) and decorating the
    /// concrete class with Mapperly's <c>[Mapper]</c> attribute:
    /// <code>
    /// [Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
    /// public partial class OrderMapper : MapperBase&lt;Order, OrderDto&gt;
    /// {
    ///     public override partial OrderDto Map(Order source);
    ///     public override partial OrderDto Map(Order source, OrderDto destination);
    /// }
    /// </code>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="mapperTypes">
    /// Marker types whose assemblies are scanned for mapper implementations
    /// (e.g. <c>[typeof(OrderMapper), typeof(UserMapper)]</c>).
    /// </param>
    public static IServiceCollection AddAetherMapperlyMapper(
        this IServiceCollection services,
        IEnumerable<Type> mapperTypes)
    {
        var assemblies = mapperTypes.Select(t => t.Assembly).Distinct();

        var mapperInterfaces = new[]
        {
            typeof(IMapperlyMapper<,>),
            typeof(IReverseMapperlyMapper<,>),
            typeof(IObjectMapper<,>)
        };

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface))
            {
                foreach (var iface in type.GetInterfaces())
                {
                    if (iface.IsGenericType && mapperInterfaces.Contains(iface.GetGenericTypeDefinition()))
                    {
                        services.AddSingleton(iface, type);
                    }
                }
            }
        }

        services.AddSingleton<IObjectMapper, MapperlyAdapter>();
        return services;
    }
}
