using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;

namespace BBT.Aether.Domain.Services;

/// <summary>
/// Manages retrieval of translations for multi-lingual entities.
/// </summary>
public class MultiLingualEntityManager : IMultiLingualEntityManager
{
    /// <summary>
    /// Default language code.
    /// </summary>
    public const string DefaultLanguage = "en-US";

    /// <summary>
    /// Maximum depth to traverse when falling back to parent cultures.
    /// </summary>
    protected const int MaxCultureFallbackDepth = 5;

    /// <summary>
    /// Gets a specific translation from a collection of translations based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="translations">The collection of translations to search.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>The matching translation, or null if no match is found.</returns>
    public virtual TTranslation? GetTranslation<TTranslation>(
        IEnumerable<TTranslation>? translations,
        string? culture,
        bool fallbackToParentCultures)
        where TTranslation : class, IEntityTranslation

    {
        culture ??= CultureInfo.CurrentUICulture.Name;

        if (translations == null || !translations.Any())
        {
            return null;
        }

        var translation = translations.FirstOrDefault(pt => pt.Language == culture);
        if (translation != null)
        {
            return translation;
        }

        if (fallbackToParentCultures)
        {
            translation = GetTranslationBasedOnCulturalRecursive(
                CultureInfo.CurrentUICulture.Parent,
                translations,
                0
            );

            if (translation != null)
            {
                return translation;
            }
        }

        translation = translations.FirstOrDefault(pt => pt.Language == DefaultLanguage);
        if (translation != null)
        {
            return translation;
        }

        translation = translations.FirstOrDefault();
        return translation;
    }

    /// <summary>
    /// Gets a translation for a multi-lingual entity based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TMultiLingual">The type of the multi-lingual entity.</typeparam>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="multiLingual">The multi-lingual entity to get the translation for.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>The matching translation, or null if no match is found.</returns>
    public virtual TTranslation? GetTranslation<TMultiLingual, TTranslation>(
        TMultiLingual multiLingual,
        string? culture = null,
        bool fallbackToParentCultures = true)
        where TMultiLingual : IMultiLingualEntity<TTranslation>
        where TTranslation : class, IEntityTranslation
    {
        return GetTranslation(multiLingual.Translations, culture: culture,
            fallbackToParentCultures: fallbackToParentCultures);
    }

    /// <summary>
    /// Recursively searches for a translation based on the parent cultures.
    /// </summary>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="culture">The culture to search for.</param>
    /// <param name="translations">The collection of translations to search.</param>
    /// <param name="currentDepth">The current recursion depth.</param>
    /// <returns>The matching translation, or null if no match is found.</returns>
    protected virtual TTranslation? GetTranslationBasedOnCulturalRecursive<TTranslation>(
        CultureInfo? culture, IEnumerable<TTranslation>? translations, int currentDepth)
        where TTranslation : class, IEntityTranslation
    {
        if (culture == null ||
            culture.Name.IsNullOrWhiteSpace() ||
            translations == null || !translations.Any() ||
            currentDepth > MaxCultureFallbackDepth)
        {
            return null;
        }

        var translation =
            translations.FirstOrDefault(pt => pt.Language.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
        return translation ?? GetTranslationBasedOnCulturalRecursive(culture.Parent, translations, currentDepth + 1);
    }

    /// <summary>
    /// Retrieves bulk translations for a collection of translation lists based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="translationsCombined">The collection of translation lists to search.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>A list of matching translations, with null entries where no match is found.</returns>
    public virtual Task<List<TTranslation?>> GetBulkTranslationsAsync<TTranslation>(
        IEnumerable<IEnumerable<TTranslation>>? translationsCombined, string? culture, bool fallbackToParentCultures)
        where TTranslation : class, IEntityTranslation
    {
        culture ??= CultureInfo.CurrentUICulture.Name;

        if (translationsCombined == null || !translationsCombined.Any())
        {
            return Task.FromResult<List<TTranslation?>>(new());
        }

        var someHaveNoTranslations = false;
        var res = new List<TTranslation?>();
        foreach (var translations in translationsCombined)
        {
            if (!translations.Any())
            {
                //if the src has no translations, don't try to find a translation
                res.Add(null);
                continue;
            }

            var translation = translations.FirstOrDefault(pt => pt.Language == culture);
            if (translation != null)
            {
                res.Add(translation);
            }
            else
            {
                if (fallbackToParentCultures)
                {
                    translation = GetTranslationBasedOnCulturalRecursive(
                        CultureInfo.CurrentUICulture.Parent,
                        translations,
                        0
                    );

                    if (translation != null)
                    {
                        res.Add(translation);
                    }
                    else
                    {
                        res.Add(null);
                        someHaveNoTranslations = true;
                    }
                }
                else
                {
                    res.Add(null);
                    someHaveNoTranslations = true;
                }
            }
        }


        if (someHaveNoTranslations)
        {
            var index = 0;
            foreach (var translations in translationsCombined)
            {
                if (!translations.Any())
                {
                    //don't try to find a translation
                }
                else
                {
                    var translation = res[index];
                    if (translation != null)
                    {
                        continue;
                    }

                    translation = translations.FirstOrDefault(pt => pt.Language == DefaultLanguage);
                    if (translation != null)
                    {
                        res[index] = translation;
                    }
                    else
                    {
                        res[index] = translations.FirstOrDefault();
                    }
                }

                index++;
            }
        }

        return Task.FromResult(res);
    }

    /// <summary>
    /// Retrieves bulk translations for a collection of multi-lingual entities based on culture and fallback settings.
    /// </summary>
    /// <typeparam name="TMultiLingual">The type of the multi-lingual entity.</typeparam>
    /// <typeparam name="TTranslation">The type of the translation entity.</typeparam>
    /// <param name="multiLinguals">The collection of multi-lingual entities to get translations for.</param>
    /// <param name="culture">The culture code to match. If null, uses the current UI culture.</param>
    /// <param name="fallbackToParentCultures">Whether to fallback to parent cultures if an exact match is not found.</param>
    /// <returns>A list of tuples containing the entity and its matching translation, with null translations where no match is found.</returns>
    public virtual async Task<List<(TMultiLingual entity, TTranslation? translation)>>
        GetBulkTranslationsAsync<TMultiLingual, TTranslation>(IEnumerable<TMultiLingual> multiLinguals, string? culture,
            bool fallbackToParentCultures)
        where TMultiLingual : IMultiLingualEntity<TTranslation>
        where TTranslation : class, IEntityTranslation
    {
        var resInitial = await GetBulkTranslationsAsync(multiLinguals.Select(x => x.Translations), culture,
            fallbackToParentCultures);
        var index = 0;
        var res = new List<(TMultiLingual entity, TTranslation? translation)>();
        foreach (var item in multiLinguals)
        {
            var t = resInitial[index++];
            res.Add((item, t));
        }

        return res;
    }
}