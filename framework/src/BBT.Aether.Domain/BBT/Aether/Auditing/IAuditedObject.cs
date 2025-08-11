namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for entities that are audited.
/// </summary>
public interface IAuditedObject : ICreationAuditedObject, IModifyAuditedObject
{
    
}