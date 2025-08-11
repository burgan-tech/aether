using System;

namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that have a modification time.
/// </summary>
public interface IHasModifyTime
{
    /// <summary>
    /// The time when the entity was last modified.
    /// </summary>
    DateTime? ModifiedAt { get; }
}