using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Domain.Entities.Auditing;

/// <summary>
/// A base class for entities that implement auditing.
/// Includes creation and modification information.
/// </summary>
public abstract class AuditedEntity : CreationAuditedEntity, IAuditedObject
{
    /// <summary>
    /// Gets or sets the last modification time for this entity.
    /// </summary>
    public virtual DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Gets or sets the user who last modified this entity.
    /// </summary>
    public virtual string? ModifiedBy { get; set; }

    /// <summary>
    /// Gets or sets the user on behalf of whom this entity was last modified.
    /// </summary>
    public virtual string? ModifiedByBehalfOf { get; set; }
}

/// <summary>
/// A base class for entities that implement auditing with a composite primary key.
/// Includes creation and modification information.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public abstract class AuditedEntity<TKey> : CreationAuditedEntity<TKey>, IAuditedObject
{
    /// <summary>
    /// Gets or sets the last modification time for this entity.
    /// </summary>
    public virtual DateTime? ModifiedAt { get; set; }
    
    /// <summary>
    /// Gets or sets the user who last modified this entity.
    /// </summary>
    public virtual string? ModifiedBy { get; set; }

    /// <summary>
    /// Gets or sets the user on behalf of whom this entity was last modified.
    /// </summary>
    public virtual string? ModifiedByBehalfOf { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditedEntity{TKey}"/> class.
    /// </summary>
    protected AuditedEntity()
    {

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditedEntity{TKey}"/> class.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    protected AuditedEntity(TKey id)
        : base(id)
    {

    }
}