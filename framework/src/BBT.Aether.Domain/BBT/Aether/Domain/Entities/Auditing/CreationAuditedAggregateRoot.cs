using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Domain.Entities.Auditing;

/// <summary>
/// A base class for aggregate roots that implement creation auditing.
/// Includes creation time and user information.
/// </summary>
public abstract class CreationAuditedAggregateRoot : AggregateRoot, ICreationAuditedObject
{
    /// <summary>
    /// Gets or sets the time this aggregate root was created.
    /// </summary>
    public virtual DateTime CreatedAt { get; protected set; }

    /// <summary>
    /// Gets or sets the user who created this aggregate root.
    /// </summary>
    public virtual string? CreatedBy { get; protected set; }

    /// <summary>
    /// Gets or sets the user on behalf of whom this aggregate root was created.
    /// </summary>
    public virtual string? CreatedByBehalfOf { get; protected set; }
}

/// <summary>
/// A base class for aggregate roots that implement creation auditing with a composite primary key.
/// Includes creation time and user information.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public abstract class CreationAuditedAggregateRoot<TKey> : AggregateRoot<TKey>, ICreationAuditedObject
{
    /// <summary>
    /// Gets or sets the time this aggregate root was created.
    /// </summary>
    public virtual DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the user who created this aggregate root.
    /// </summary>
    public virtual string? CreatedBy { get; set; }

    /// <summary>
    /// Gets or sets the user on behalf of whom this aggregate root was created.
    /// </summary>
    public virtual string? CreatedByBehalfOf { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CreationAuditedAggregateRoot{TKey}"/> class.
    /// </summary>
    protected CreationAuditedAggregateRoot()
    {

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CreationAuditedAggregateRoot{TKey}"/> class.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    protected CreationAuditedAggregateRoot(TKey id)
        : base(id)
    {

    }
}