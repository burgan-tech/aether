namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that are creation audited.
/// </summary>
public interface ICreationAuditedObject : IHasCreatedAt
{
    /// <summary>
    /// User who created the entity.
    /// </summary>
    string? CreatedBy { get; }

    /// <summary>
    /// User on whose behalf the entity was created.
    /// </summary>
    string? CreatedByBehalfOf { get; }
}