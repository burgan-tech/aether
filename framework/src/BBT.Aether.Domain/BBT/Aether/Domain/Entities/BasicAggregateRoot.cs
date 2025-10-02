using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Events.Distributed;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// A basic implementation of <see cref="IAggregateRoot"/>.
/// </summary>
[Serializable]
public abstract class BasicAggregateRoot : Entity,
    IAggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = new();
    private readonly List<IDistributedDomainEvent> _distributedEvents = new();
    private long _version = 0;

    /// <summary>
    /// Gets the domain events that have been raised by this aggregate root.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Gets the distributed domain events that have been raised by this aggregate root.
    /// </summary>
    public IReadOnlyCollection<IDistributedDomainEvent> DistributedEvents => _distributedEvents.AsReadOnly();

    /// <summary>
    /// Gets the current version of this aggregate root.
    /// </summary>
    public long EventVersion => _version;

    /// <summary>
    /// Raises a local domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to raise.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        if (domainEvent == null)
            throw new ArgumentNullException(nameof(domainEvent));

        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Raises a distributed domain event.
    /// </summary>
    /// <param name="distributedEvent">The distributed domain event to raise.</param>
    protected void RaiseDistributed(IDistributedDomainEvent distributedEvent)
    {
        if (distributedEvent == null)
            throw new ArgumentNullException(nameof(distributedEvent));

        _distributedEvents.Add(distributedEvent);
        _version++; // Increment version for each distributed event
    }

    /// <summary>
    /// Clears all domain events from this aggregate root.
    /// </summary>
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    /// <summary>
    /// Clears all distributed events from this aggregate root.
    /// </summary>
    public void ClearDistributedEvents()
    {
        _distributedEvents.Clear();
    }

    /// <summary>
    /// Clears all events (both local and distributed) from this aggregate root.
    /// </summary>
    public void ClearAllEvents()
    {
        _domainEvents.Clear();
        _distributedEvents.Clear();
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
    private readonly List<IDistributedDomainEvent> _distributedEvents = new();
    private long _version = 0;

    /// <summary>
    /// Gets the domain events that have been raised by this aggregate root.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Gets the distributed domain events that have been raised by this aggregate root.
    /// </summary>
    public IReadOnlyCollection<IDistributedDomainEvent> DistributedEvents => _distributedEvents.AsReadOnly();

    /// <summary>
    /// Gets the current version of this aggregate root.
    /// </summary>
    public long EventVersion => _version;

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
    /// Raises a local domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to raise.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        if (domainEvent == null)
            throw new ArgumentNullException(nameof(domainEvent));

        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Raises a distributed domain event.
    /// </summary>
    /// <param name="distributedEvent">The distributed domain event to raise.</param>
    protected void RaiseDistributed(IDistributedDomainEvent distributedEvent)
    {
        if (distributedEvent == null)
            throw new ArgumentNullException(nameof(distributedEvent));

        _distributedEvents.Add(distributedEvent);
        _version++; // Increment version for each distributed event
    }

    /// <summary>
    /// Clears all domain events from this aggregate root.
    /// </summary>
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    /// <summary>
    /// Clears all distributed events from this aggregate root.
    /// </summary>
    public void ClearDistributedEvents()
    {
        _distributedEvents.Clear();
    }

    /// <summary>
    /// Clears all events (both local and distributed) from this aggregate root.
    /// </summary>
    public void ClearAllEvents()
    {
        _domainEvents.Clear();
        _distributedEvents.Clear();
    }
}