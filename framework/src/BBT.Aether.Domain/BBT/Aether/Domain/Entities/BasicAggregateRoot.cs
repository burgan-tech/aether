using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Events;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// A basic implementation of <see cref="IAggregateRoot"/>.
/// </summary>
[Serializable]
public abstract class BasicAggregateRoot : Entity,
    IAggregateRoot,
    IHasDomainEvents
{
    private readonly List<DomainEventEnvelope> _domainEvents = new();

    /// <summary>
    /// Adds a distributed event to be published after the aggregate is persisted.
    /// Events are dispatched after SaveChanges completes successfully.
    /// Event metadata (EventName, Version, PubSubName) is extracted from EventNameAttribute at this point.
    /// </summary>
    /// <param name="event">The distributed event to add</param>
    /// <exception cref="InvalidOperationException">Thrown if the event doesn't have EventNameAttribute</exception>
    protected void AddDistributedEvent(IDistributedEvent @event)
    {
        // Extract metadata once at the time of adding the event
        var metadata = EventMetadataExtractor.Extract(@event);
        var envelope = new DomainEventEnvelope(@event, metadata);
        _domainEvents.Add(envelope);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents()
    {
        return _domainEvents.AsReadOnly();
    }

    /// <inheritdoc />
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
    IAggregateRoot<TKey>,
    IHasDomainEvents
{
    private readonly List<DomainEventEnvelope> _domainEvents = new();

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
    /// Adds a distributed event to be published after the aggregate is persisted.
    /// Events are dispatched after SaveChanges completes successfully.
    /// Event metadata (EventName, Version, PubSubName) is extracted from EventNameAttribute at this point.
    /// </summary>
    /// <param name="event">The distributed event to add</param>
    /// <exception cref="InvalidOperationException">Thrown if the event doesn't have EventNameAttribute</exception>
    protected void AddDistributedEvent(IDistributedEvent @event)
    {
        // Extract metadata once at the time of adding the event
        var metadata = EventMetadataExtractor.Extract(@event);
        var envelope = new DomainEventEnvelope(@event, metadata);
        _domainEvents.Add(envelope);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DomainEventEnvelope> GetDomainEvents()
    {
        return _domainEvents.AsReadOnly();
    }

    /// <inheritdoc />
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}