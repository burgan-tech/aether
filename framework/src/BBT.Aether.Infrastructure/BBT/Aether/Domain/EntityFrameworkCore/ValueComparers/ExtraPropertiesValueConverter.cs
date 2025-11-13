using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BBT.Aether.Domain.EntityFrameworkCore.ValueComparers;

/// <summary>
/// Value converter for ExtraPropertyDictionary that serializes to/from JSON string.
/// </summary>
/// <typeparam name="TEntityType">The entity type that has ExtraProperties</typeparam>
public class ExtraPropertiesValueConverter<TEntityType> : ValueConverter<ExtraPropertyDictionary, string>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ExtraPropertiesValueConverter()
        : base(
            extraProperties => SerializeObject(extraProperties),
            json => DeserializeObject(json))
    {
    }

    private static string SerializeObject(ExtraPropertyDictionary extraProperties)
    {
        if (extraProperties == null || extraProperties.Count == 0)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(extraProperties, JsonOptions);
    }

    private static ExtraPropertyDictionary DeserializeObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new ExtraPropertyDictionary();
        }

        try
        {
            var dictionary = JsonSerializer.Deserialize<ExtraPropertyDictionary>(json, JsonOptions);
            return dictionary ?? new ExtraPropertyDictionary();
        }
        catch
        {
            // If deserialization fails, return empty dictionary
            return new ExtraPropertyDictionary();
        }
    }
}