using System;
using System.Linq;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Default schema name formatter that applies common database naming conventions:
/// <list type="bullet">
/// <item><description>Converts to lowercase</description></item>
/// <item><description>Replaces spaces and hyphens with underscores</description></item>
/// <item><description>Removes special characters (keeps only letters, digits, and underscore)</description></item>
/// <item><description>Ensures schema starts with a letter or underscore</description></item>
/// <item><description>Trims to maximum length (63 characters for PostgreSQL compatibility)</description></item>
/// </list>
/// </summary>
public class DefaultSchemaNameFormatter : ISchemaNameFormatter
{
    private const int MaxLength = 63; // PostgreSQL identifier limit
    
    /// <inheritdoc />
    public string Format(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentException("Schema name cannot be null or whitespace.", nameof(schemaName));

        // Convert to lowercase
        var formatted = schemaName.ToLowerInvariant();
        
        // Replace spaces and hyphens with underscores
        formatted = formatted.Replace(' ', '_').Replace('-', '_');
        
        // Remove invalid characters (keep only letters, digits, underscore)
        formatted = new string(formatted
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
        
        // Ensure it starts with a letter or underscore
        if (!string.IsNullOrEmpty(formatted) && !char.IsLetter(formatted[0]) && formatted[0] != '_')
        {
            formatted = "_" + formatted;
        }
        
        // Trim to max length
        if (formatted.Length > MaxLength)
        {
            formatted = formatted.Substring(0, MaxLength);
        }
        
        if (string.IsNullOrWhiteSpace(formatted))
            throw new InvalidOperationException($"Schema name '{schemaName}' could not be formatted to a valid database schema name.");
        
        return formatted;
    }
}

