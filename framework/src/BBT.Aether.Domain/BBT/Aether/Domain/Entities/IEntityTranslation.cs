namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Interface for entities that support translation.
/// </summary>
public interface IEntityTranslation
{
    /// <summary>
    /// Gets or sets the language of the translation.
    /// </summary>
    string Language { get; set; }
}