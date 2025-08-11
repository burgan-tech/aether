namespace BBT.Aether.Mapper;

/// <summary>
/// Defines a generic object mapper interface.
/// </summary>
public interface IObjectMapper
{
    /// <summary>
    /// Maps a <typeparamref name="TSource"/> object to a new <typeparamref name="TDestination"/> object.
    /// </summary>
    /// <typeparam name="TSource">The type of the source object.</typeparam>
    /// <typeparam name="TDestination">The type of the destination object.</typeparam>
    /// <param name="source">The source object to map from.</param>
    /// <returns>A new <typeparamref name="TDestination"/> object with values mapped from the <paramref name="source"/> object.</returns>
    TDestination Map<TSource, TDestination>(TSource source);

    /// <summary>
    /// Maps a <typeparamref name="TSource"/> object to an existing <typeparamref name="TDestination"/> object.
    /// </summary>
    /// <typeparam name="TSource">The type of the source object.</typeparam>
    /// <typeparam name="TDestination">The type of the destination object.</typeparam>
    /// <param name="source">The source object to map from.</param>
    /// <param name="destination">The existing destination object to map to.</param>
    void Map<TSource, TDestination>(TSource source, TDestination destination);
}

/// <summary>
/// Defines a generic object mapper interface with specific source and destination types.
/// </summary>
/// <typeparam name="TSource">The type of the source object.</typeparam>
/// <typeparam name="TDestination">The type of the destination object.</typeparam>
public interface IObjectMapper<in TSource, TDestination>
{
    /// <summary>
    /// Maps a <typeparamref name="TSource"/> object to a new <typeparamref name="TDestination"/> object.
    /// </summary>
    /// <param name="source">The source object to map from.</param>
    /// <returns>A new <typeparamref name="TDestination"/> object with values mapped from the <paramref name="source"/> object.</returns>
    TDestination Map(TSource source);

    /// <summary>
    /// Maps a <typeparamref name="TSource"/> object to an existing <typeparamref name="TDestination"/> object.
    /// </summary>
    /// <param name="source">The source object to map from.</param>
    /// <param name="destination">The existing destination object to map to.</param>
    /// <returns>The <paramref name="destination"/> object with values mapped from the <paramref name="source"/> object.</returns>
    TDestination Map(TSource source, TDestination destination);
}