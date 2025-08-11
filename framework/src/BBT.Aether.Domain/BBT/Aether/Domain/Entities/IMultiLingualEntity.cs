using System.Collections.Generic;

namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Defines an entity that supports multiple languages.
/// </summary>
/// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
public interface IMultiLingualEntity<TTranslation>
    where TTranslation : class, IEntityTranslation
{
    /// <summary>
    /// Gets or sets the translations for this entity.
    /// </summary>
    ICollection<TTranslation> Translations { get; set; }
}