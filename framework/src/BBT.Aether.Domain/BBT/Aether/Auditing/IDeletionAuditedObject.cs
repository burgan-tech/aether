namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that are deletion audited.
/// </summary>
public interface IDeletionAuditedObject : IHasDeletionTime
{
    /// <summary>
    /// User who deleted the entity.
    /// </summary>
    string? DeletedBy { get; }
}