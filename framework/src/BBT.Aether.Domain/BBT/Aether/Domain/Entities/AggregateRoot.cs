namespace BBT.Aether.Domain.Entities;

/// <summary>
/// An aggregate root entity.
/// </summary>
public abstract class AggregateRoot : BasicAggregateRoot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot"/> class.
    /// </summary>
    protected AggregateRoot()
    {
    }
}

/// <summary>
/// An aggregate root entity with a key.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
public abstract class AggregateRoot<TKey> : BasicAggregateRoot<TKey>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot{TKey}"/> class.
    /// </summary>
    protected AggregateRoot()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot{TKey}"/> class with the specified ID.
    /// </summary>
    /// <param name="id">The ID of the entity.</param>
    protected AggregateRoot(TKey id)
        : base(id)
    {
    }
}