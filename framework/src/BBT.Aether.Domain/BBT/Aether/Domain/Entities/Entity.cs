using System.Collections.Generic;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Base class for entities.
/// </summary>
public abstract class Entity : IEntity
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Entity"/> class.
    /// </summary>
    protected Entity()
    {
        
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{GetType().Name} Keys = {GetKeys().JoinAsString(", ")}";
    }

    public abstract object?[] GetKeys();
}

public abstract class Entity<TKey> : Entity, IEntity<TKey>
{
    public virtual TKey Id { get; protected set; } = default!;

    protected Entity()
    {

    }

    protected Entity(TKey id)
    {
        Id = id;
    }

    public override object?[] GetKeys()
    {
        return new object?[] { Id };
    }
    
    public override string ToString()
    {
        return $"[{GetType().Name}] Id = {Id}";
    }
}