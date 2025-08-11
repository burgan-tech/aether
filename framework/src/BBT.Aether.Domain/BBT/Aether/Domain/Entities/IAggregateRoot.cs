namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Defines the interface for an aggregate root.
/// </summary>
public interface IAggregateRoot : IEntity
{
}

/// <summary>
/// Defines the interface for an aggregate root with a key.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public interface IAggregateRoot<TKey> : IEntity<TKey>, IAggregateRoot
{
}