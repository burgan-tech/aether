using System;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// An aggregate root entity.
/// </summary>
public abstract class AggregateRoot : BasicAggregateRoot, 
    IHasConcurrencyStamp
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot"/> class.
    /// </summary>
    protected AggregateRoot()
    {
        ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }
    
    /// <inheritdoc />
    public virtual string ConcurrencyStamp { get; set; }
}

/// <summary>
/// An aggregate root entity with a key.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
public abstract class AggregateRoot<TKey> : BasicAggregateRoot<TKey>, IHasConcurrencyStamp
{
    /// <inheritdoc />
    public virtual string ConcurrencyStamp { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot{TKey}"/> class.
    /// </summary>
    protected AggregateRoot()
    {
        ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot{TKey}"/> class with the specified ID.
    /// </summary>
    /// <param name="id">The ID of the entity.</param>
    protected AggregateRoot(TKey id)
        : base(id)
    {
        ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }
}