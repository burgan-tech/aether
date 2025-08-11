using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Domain.Entities.Auditing;

/// <summary>
/// A base class for entities that implement creation auditing.
/// Includes creation time and user information.
/// </summary>
public abstract class CreationAuditedEntity : Entity, ICreationAuditedObject
{
    /// <summary>
    /// Gets the time this entity was created.
    /// </summary>
    public virtual DateTime CreatedAt { get; protected set; }
    
    /// <summary>
    /// Gets the user who created this entity.
    /// </summary>
    public virtual string? CreatedBy { get; protected set; }

    /// <summary>
    /// Gets the user on behalf of whom this entity was created.
    /// </summary>
    public virtual string? CreatedByBehalfOf { get; protected set; }
}

/// <summary>
/// A base class for entities that implement creation auditing with a composite primary key.
/// Includes creation time and user information.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public abstract class CreationAuditedEntity<TKey> : Entity<TKey>, ICreationAuditedObject
{
    /// <summary>
    /// Gets the time this entity was created.
    /// </summary>
    public virtual DateTime CreatedAt { get; protected set; }
    
    /// <summary>
    /// Gets the user who created this entity.
    /// </summary>
    public virtual string? CreatedBy { get; protected set; }

    /// <summary>
    /// Gets the user on behalf of whom this entity was created.
    /// </summary>
    public virtual string? CreatedByBehalfOf { get; protected set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CreationAuditedEntity{TKey}"/> class.
    /// </summary>
    protected CreationAuditedEntity()
    {

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CreationAuditedEntity{TKey}"/> class.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    protected CreationAuditedEntity(TKey id)
        : base(id)
    {

    }
}