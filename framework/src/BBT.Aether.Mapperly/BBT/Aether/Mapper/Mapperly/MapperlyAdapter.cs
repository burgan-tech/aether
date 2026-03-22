using System;
using BBT.Aether.Mapper;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Mapper.Mapperly;

/// <summary>
/// An <see cref="IObjectMapper"/> implementation that dispatches mapping calls to
/// registered <see cref="IMapperlyMapper{TSource,TDestination}"/> instances resolved from DI,
/// invoking lifecycle hooks (<c>BeforeMap</c> / <c>AfterMap</c>) around each call.
/// For the reverse direction, falls back to <see cref="IReverseMapperlyMapper{TSource,TDestination}"/>
/// when no direct forward mapper is found.
/// </summary>
public class MapperlyAdapter(IServiceProvider serviceProvider) : IObjectMapper
{
    /// <inheritdoc />
    public TDestination Map<TSource, TDestination>(TSource source)
    {
        var mapper = serviceProvider.GetService<IMapperlyMapper<TSource, TDestination>>();
        if (mapper is not null)
        {
            mapper.BeforeMap(source);
            var destination = mapper.Map(source);
            mapper.AfterMap(source, destination);
            return destination;
        }

        // Reverse fallback: look for a TwoWayMapper registered for the opposite direction.
        var reverseMapper = serviceProvider.GetService<IReverseMapperlyMapper<TDestination, TSource>>();
        if (reverseMapper is not null)
        {
            reverseMapper.BeforeReverseMap(source);
            var destination = reverseMapper.ReverseMap(source);
            reverseMapper.AfterReverseMap(source, destination);
            return destination;
        }

        throw new InvalidOperationException(
            $"No mapper registered for {typeof(TSource).Name} → {typeof(TDestination).Name}. " +
            $"Ensure a {nameof(MapperBase<TSource, TDestination>)} or " +
            $"{nameof(TwoWayMapperBase<TDestination, TSource>)} implementation is registered via AddAetherMapperlyMapper.");
    }

    /// <inheritdoc />
    public void Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        var mapper = serviceProvider.GetService<IMapperlyMapper<TSource, TDestination>>();
        if (mapper is not null)
        {
            mapper.BeforeMap(source);
            mapper.Map(source, destination);
            mapper.AfterMap(source, destination);
            return;
        }
        
        // Reverse fallback: look for a TwoWayMapper registered for the opposite direction.
        var reverseMapper = serviceProvider.GetService<IReverseMapperlyMapper<TDestination, TSource>>();
        if (reverseMapper is not null)
        {
            reverseMapper.BeforeReverseMap(source);
            reverseMapper.ReverseMap(source, destination);
            reverseMapper.AfterReverseMap(source, destination);
            return;
        }

        throw new InvalidOperationException(
            $"No mapper registered for {typeof(TSource).Name} → {typeof(TDestination).Name}. " +
            $"Ensure a {nameof(MapperBase<TSource, TDestination>)} implementation is registered via AddAetherMapperlyMapper.");
    }
}
