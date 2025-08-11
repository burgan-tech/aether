using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Domain.Entities.Auditing;

/// <summary>
/// A base class for aggregate roots that fully implement auditing.
/// Includes creation, modification, and deletion information.
/// </summary>
public abstract class FullAuditedAggregateRoot: AuditedAggregateRoot, IFullAuditedObject
{
    /// <summary>
    /// Gets or sets a value indicating whether this aggregate root is deleted.
    /// </summary>
    public virtual bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the user who deleted this aggregate root.
    /// </summary>
    public virtual string? DeletedBy { get; set; }

    /// <summary>
    /// Gets or sets the time this aggregate root was deleted.
    /// </summary>
    public virtual DateTime? DeletedAt { get; set; }
}

/// <summary>
/// A base class for aggregate roots that fully implement auditing with a composite primary key.
/// Includes creation, modification, and deletion information.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
public abstract class FullAuditedAggregateRoot<TKey> : AuditedAggregateRoot<TKey>, IFullAuditedObject
{
    /// <summary>
    /// Gets or sets a value indicating whether this aggregate root is deleted.
    /// </summary>
    public virtual bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the user who deleted this aggregate root.
    /// </summary>
    public virtual string? DeletedBy { get; set; }

    /// <summary>
    /// Gets or sets the time this aggregate root was deleted.
    /// </summary>
    public virtual DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FullAuditedAggregateRoot{TKey}"/> class.
    /// </summary>
    protected FullAuditedAggregateRoot()
    {

    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FullAuditedAggregateRoot{TKey}"/> class.
    /// </summary>
    /// <param name="id">The primary key value.</param>
    protected FullAuditedAggregateRoot(TKey id)
        : base(id)
    {

    }
}