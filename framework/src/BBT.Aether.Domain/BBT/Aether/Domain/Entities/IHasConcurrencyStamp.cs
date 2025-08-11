namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Interface for entities that have a concurrency stamp.
/// </summary>
public interface IHasConcurrencyStamp
{
    /// <summary>
    /// Gets or sets the concurrency stamp.
    /// </summary>
    string ConcurrencyStamp { get; set; }
}
