using BBT.Aether.Mapper;

namespace BBT.Aether.Mapper.Mapperly;

/// <summary>
/// Abstract base class for one-way Mapperly mappers.
/// Decorate the concrete subclass with Mapperly's <c>[Mapper]</c> attribute and declare
/// the generated mapping methods as <c>override partial</c>.
/// </summary>
/// <example>
/// <code>
/// [Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
/// public partial class OrderMapper : MapperBase&lt;Order, OrderDto&gt;
/// {
///     public override partial OrderDto Map(Order source);
///     public override partial OrderDto Map(Order source, OrderDto destination);
/// }
/// </code>
/// </example>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public abstract class MapperBase<TSource, TDestination>
    : IMapperlyMapper<TSource, TDestination>, IObjectMapper<TSource, TDestination>
{
    /// <inheritdoc />
    public abstract TDestination Map(TSource source);

    /// <inheritdoc />
    public abstract TDestination Map(TSource source, TDestination destination);

    /// <inheritdoc />
    public virtual void BeforeMap(TSource source) { }

    /// <inheritdoc />
    public virtual void AfterMap(TSource source, TDestination destination) { }
}
