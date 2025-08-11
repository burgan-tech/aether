using System;

namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that have a deletion time.
/// </summary>
public interface IHasDeletionTime : ISoftDelete
{
    /// <summary>
    /// The time when the entity was deleted.
    /// </summary>
    DateTime? DeletedAt { get; }
}