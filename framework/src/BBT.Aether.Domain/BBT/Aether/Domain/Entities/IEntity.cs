namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Defines the basic interface for an entity.
/// </summary>
public interface IEntity
{
    /// <summary>
    /// Gets the composite keys for this entity.
    /// </summary>
    object?[] GetKeys();
}

/// <summary>
/// Defines the basic interface for an entity with a key.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public interface IEntity<TKey> : IEntity
{
    /// <summary>
    /// Unique id
    /// </summary>
    TKey Id { get; }
}