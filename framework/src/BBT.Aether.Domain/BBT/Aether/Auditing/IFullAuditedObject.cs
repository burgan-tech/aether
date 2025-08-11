namespace BBT.Aether.Auditing;

/// <summary>
/// Interface for fully audited entities.
/// </summary>
public interface IFullAuditedObject : IAuditedObject, IDeletionAuditedObject
{

}