namespace BBT.Aether.Mapper.Mapperly;

/// <summary>
/// A Mapperly-specific mapper interface that adds lifecycle hooks around the mapping operation.
/// Implement this interface via <see cref="MapperBase{TSource,TDestination}"/> and decorate the
/// concrete class with Mapperly's <c>[Mapper]</c> attribute.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public interface IMapperlyMapper<TSource, TDestination>
{
    /// <summary>Maps <paramref name="source"/> to a new <typeparamref name="TDestination"/> instance.</summary>
    TDestination Map(TSource source);

    /// <summary>Maps <paramref name="source"/> into the existing <paramref name="destination"/> instance.</summary>
    TDestination Map(TSource source, TDestination destination);

    /// <summary>Called before <see cref="Map(TSource)"/> or <see cref="Map(TSource,TDestination)"/>.</summary>
    void BeforeMap(TSource source);

    /// <summary>Called after <see cref="Map(TSource)"/> or <see cref="Map(TSource,TDestination)"/>.</summary>
    void AfterMap(TSource source, TDestination destination);
}
