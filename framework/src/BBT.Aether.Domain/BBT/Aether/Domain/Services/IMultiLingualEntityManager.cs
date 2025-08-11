using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Interface for managing retrieval of translations for multi-lingual entities.
/// </summary>
public interface IMultiLingualEntityManager
{
    /// <summary>
    /// Gets a translation for a multi-lingual entity based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TMultiLingual">The type of the multi-lingual entity.</typeparam>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="multiLingual">The multi-lingual entity to get the translation for.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>The matching translation, or null if no match is found.</returns>
    TTranslation? GetTranslation<TMultiLingual, TTranslation>(
        TMultiLingual multiLingual,
        string? culture = null,
        bool fallbackToParentCultures = true)
        where TMultiLingual : IMultiLingualEntity<TTranslation>
        where TTranslation : class, IEntityTranslation;

    /// <summary>
    /// Gets a specific translation from a collection of translations based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="translations">The collection of translations to search.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>The matching translation, or null if no match is found.</returns>
    TTranslation? GetTranslation<TTranslation>(
        IEnumerable<TTranslation> translations,
        string? culture = null,
        bool fallbackToParentCultures = true)
        where TTranslation : class, IEntityTranslation;

    /// <summary>
    /// Retrieves bulk translations for a collection of translation lists based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="translationsCombined">The collection of translation lists to search.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>A list of matching translations, with null entries where no match is found.</returns>
    Task<List<TTranslation?>> GetBulkTranslationsAsync<TTranslation>(
        IEnumerable<IEnumerable<TTranslation>> translationsCombined,
        string? culture = null,
        bool fallbackToParentCultures = true)
        where TTranslation : class, IEntityTranslation;

    /// <summary>
    /// Retrieves bulk translations for a collection of multi-lingual entities based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TMultiLingual">The type of the multi-lingual entity.</typeparam>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="multiLinguals">The collection of multi-lingual entities to get translations for.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>A list of tuples containing the entity and its matching translation, with null translations where no match is found.</returns>
    Task<List<(TMultiLingual entity, TTranslation? translation)>> GetBulkTranslationsAsync<TMultiLingual, TTranslation>(
        IEnumerable<TMultiLingual> multiLinguals,
        string? culture = null,
        bool fallbackToParentCultures = true)
        where TMultiLingual : IMultiLingualEntity<TTranslation>
        where TTranslation : class, IEntityTranslation;
}