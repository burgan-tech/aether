using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Domain.Entities.Auditing;

/// <summary>
/// A base class for entities that fully implement auditing.
/// Includes creation, modification, and deletion information.
/// </summary>
public abstract class FullAuditedEntity : AuditedEntity, IFullAuditedObject
{
    /// <summary>
    /// Gets or sets a value indicating whether this entity is deleted.
    /// </summary>
    public virtual bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the user who deleted this entity.
    /// </summary>
    public virtual string? DeletedBy { get; set; }

    /// <summary>
    /// Gets or sets the time this entity was deleted.
    /// </summary>
    public virtual DateTime? DeletedAt { get; set; }
}

/// <summary>
/// A base class for entities that fully implement auditing with a composite primary key.
/// Includes creation, modification, and deletion information.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public abstract class FullAuditedEntity<TKey> : AuditedEntity<TKey>, IFullAuditedObject
{
    /// <summary>
    /// Gets or sets a value indicating whether this entity is deleted.
    /// </summary>
    public virtual bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the user who deleted this entity.
    /// </summary>
    public virtual string? DeletedBy { get; set; }

    /// <summary>
    /// Gets or sets the time this entity was deleted.
    /// </summary>
    public virtual DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FullAuditedEntity{TKey}"/> class.
    /// </summary>
    protected FullAuditedEntity()
    {

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FullAuditedEntity{TKey}"/> class.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    protected FullAuditedEntity(TKey id)
        : base(id)
    {

    }
}