using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Domain.Events;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// A basic implementation of <see cref="IAggregateRoot"/>.
/// </summary>
[Serializable]
public abstract class BasicAggregateRoot : Entity,
    IAggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>
    /// Gets the domain events that have been raised by this aggregate root.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Raises a domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to raise.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        if (domainEvent == null)
            throw new ArgumentNullException(nameof(domainEvent));

        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Clears all domain events from this aggregate root.
    /// </summary>
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}

/// <summary>
/// A basic implementation of <see cref="IAggregateRoot{TKey}"/>.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
[Serializable]
public abstract class BasicAggregateRoot<TKey> : Entity<TKey>,
    IAggregateRoot<TKey>
{
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>
    /// Gets the domain events that have been raised by this aggregate root.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

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

    /// <summary>
    /// Raises a domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to raise.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        if (domainEvent == null)
            throw new ArgumentNullException(nameof(domainEvent));

        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Clears all domain events from this aggregate root.
    /// </summary>
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}