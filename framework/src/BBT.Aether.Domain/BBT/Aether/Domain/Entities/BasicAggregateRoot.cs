using System;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// A basic implementation of <see cref="IAggregateRoot"/>.
/// </summary>
[Serializable]
public abstract class BasicAggregateRoot : Entity,
    IAggregateRoot
{
}

/// <summary>
/// A basic implementation of <see cref="IAggregateRoot{TKey}"/>.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
[Serializable]
public abstract class BasicAggregateRoot<TKey> : Entity<TKey>,
    IAggregateRoot<TKey>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BasicAggregateRoot{TKey}"/> class.
    /// </summary>
    protected BasicAggregateRoot()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicAggregateRoot{TKey}"/> class with the specified ID.
    /// </summary>
    /// <param name="id">The ID of the entity.</param>
    protected BasicAggregateRoot(TKey id)
        : base(id)
    {
    }
}