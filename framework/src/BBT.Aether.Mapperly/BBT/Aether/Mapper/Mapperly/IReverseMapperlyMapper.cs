namespace BBT.Aether.Mapper.Mapperly;

/// <summary>
/// Extends <see cref="IMapperlyMapper{TSource,TDestination}"/> with a reverse mapping direction
/// (<typeparamref name="TDestination"/> → <typeparamref name="TSource"/>) and its lifecycle hooks.
/// Implement via <see cref="TwoWayMapperBase{TSource,TDestination}"/>.
/// </summary>
/// <typeparam name="TSource">The primary source type (forward direction).</typeparam>
/// <typeparam name="TDestination">The primary destination type (forward direction).</typeparam>
public interface IReverseMapperlyMapper<TSource, TDestination> : IMapperlyMapper<TSource, TDestination>
{
    /// <summary>Maps <paramref name="destination"/> back to a new <typeparamref name="TSource"/> instance.</summary>
    TSource ReverseMap(TDestination destination);

    /// <summary>Maps <paramref name="destination"/> into the existing <paramref name="source"/> instance.</summary>
    void ReverseMap(TDestination destination, TSource source);

    /// <summary>Called before <see cref="ReverseMap(TDestination)"/> or <see cref="ReverseMap(TDestination,TSource)"/>.</summary>
    void BeforeReverseMap(TDestination destination);

    /// <summary>Called after <see cref="ReverseMap(TDestination)"/> or <see cref="ReverseMap(TDestination,TSource)"/>.</summary>
    void AfterReverseMap(TDestination destination, TSource source);
}
