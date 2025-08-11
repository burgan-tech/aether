using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Domain.Entities.Auditing;

/// <summary>
/// A base class for aggregate roots that implement auditing.
/// Includes creation and modification information.
/// </summary>
public abstract class AuditedAggregateRoot: CreationAuditedAggregateRoot, IAuditedObject
{
    /// <summary>
    /// Gets or sets the last modification time for this aggregate root.
    /// </summary>
    public virtual DateTime? ModifiedAt { get; set; }
    
    /// <summary>
    /// Gets or sets the user who last modified this aggregate root.
    /// </summary>
    public virtual string? ModifiedBy { get; set; }

    /// <summary>
    /// Gets or sets the user on behalf of whom this aggregate root was last modified.
    /// </summary>
    public virtual string? ModifiedByBehalfOf { get; set;}
}

/// <summary>
/// A base class for aggregate roots that implement auditing with a composite primary key.
/// Includes creation and modification information.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public abstract class AuditedAggregateRoot<TKey> : CreationAuditedAggregateRoot<TKey>, IAuditedObject
{
    /// <summary>
    /// Gets or sets the last modification time for this aggregate root.
    /// </summary>
    public virtual DateTime? ModifiedAt { get; set; }
    /// <summary>
    /// Gets or sets the user who last modified this aggregate root.
    /// </summary>
    public virtual string? ModifiedBy { get; set; }
    /// <summary>
    /// Gets or sets the user on behalf of whom this aggregate root was last modified.
    /// </summary>
    public virtual string? ModifiedByBehalfOf { get; set;}

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditedAggregateRoot{TKey}"/> class.
    /// </summary>
    protected AuditedAggregateRoot()
    {

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditedAggregateRoot{TKey}"/> class.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    protected AuditedAggregateRoot(TKey id)
        : base(id)
    {

    }
}