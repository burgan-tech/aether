using System;

namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that are modified.
/// </summary>
public interface IModifyAuditedObject : IHasModifyTime
{
    /// <summary>
    /// User who modified the entity.
    /// </summary>
    string? ModifiedBy { get; }

    /// <summary>
    /// User on whose behalf the entity was modified.
    /// </summary>
    string? ModifiedByBehalfOf { get; }
}