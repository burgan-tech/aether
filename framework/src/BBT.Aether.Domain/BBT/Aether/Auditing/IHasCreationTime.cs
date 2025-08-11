using System;

namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that have a creation time.
/// </summary>
public interface IHasCreatedAt
{
    /// <summary>
    /// The time when the entity was created.
    /// </summary>
    DateTime CreatedAt { get; }
}