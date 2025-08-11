namespace BBT.Aether.Auditing;

/// <summary>
/// Enum representing the type of change made to an entity.
/// </summary>
public enum EntityChangeType : byte
{
    /// <summary>
    /// Entity was created.
    /// </summary>
    Created = 0,

    /// <summary>
    /// Entity was updated.
    /// </summary>
    Updated = 1,

    /// <summary>
    /// Entity was deleted.
    /// </summary>
    Deleted = 2
}
