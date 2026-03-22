namespace BBT.Aether.Mapper.Mapperly;

/// <summary>
/// Abstract base class for bidirectional Mapperly mappers.
/// Extends <see cref="MapperBase{TSource,TDestination}"/> with a reverse mapping direction
/// (<typeparamref name="TDestination"/> → <typeparamref name="TSource"/>).
/// </summary>
/// <example>
/// <code>
/// [Mapper]
/// public partial class OrderMapper : TwoWayMapperBase&lt;Order, OrderDto&gt;
/// {
///     public override partial OrderDto Map(Order source);
///     public override partial OrderDto Map(Order source, OrderDto destination);
///     public override partial Order ReverseMap(OrderDto destination);
///     public override partial void ReverseMap(OrderDto destination, Order source);
/// }
/// </code>
/// </example>
/// <typeparam name="TSource">The primary source type (forward direction).</typeparam>
/// <typeparam name="TDestination">The primary destination type (forward direction).</typeparam>
public abstract class TwoWayMapperBase<TSource, TDestination>
    : MapperBase<TSource, TDestination>, IReverseMapperlyMapper<TSource, TDestination>
{
    /// <inheritdoc />
    public abstract TSource ReverseMap(TDestination destination);

    /// <inheritdoc />
    public abstract void ReverseMap(TDestination destination, TSource source);

    /// <inheritdoc />
    public virtual void BeforeReverseMap(TDestination destination) { }

    /// <inheritdoc />
    public virtual void AfterReverseMap(TDestination destination, TSource source) { }
}
