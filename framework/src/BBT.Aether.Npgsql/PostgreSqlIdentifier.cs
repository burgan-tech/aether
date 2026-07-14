using System.Text.RegularExpressions;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Validates and quotes PostgreSQL identifiers. Identifiers cannot be passed as SQL
/// parameters, so they must be validated before interpolation into SQL text.
/// </summary>
public static class PostgreSqlIdentifier
{
    private static readonly Regex ValidIdentifier =
        new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Validates the supplied schema name and returns it as a double-quoted PostgreSQL identifier.
    /// </summary>
    /// <param name="schema">The schema name to validate and quote.</param>
    /// <returns>The schema name wrapped in double quotes, suitable for SQL interpolation.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when the schema name is not a valid PostgreSQL identifier.</exception>
    /// <exception cref="System.ArgumentException">Thrown when the schema name exceeds PostgreSQL's 63-byte identifier limit.</exception>
    public static string QuoteSchema(string schema) => Quote(schema, nameof(schema));

    /// <summary>
    /// Validates the supplied table name and returns it as a double-quoted PostgreSQL identifier.
    /// </summary>
    public static string QuoteTable(string table) => Quote(table, nameof(table));

    private static string Quote(string identifier, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !ValidIdentifier.IsMatch(identifier))
        {
            throw new System.InvalidOperationException($"Invalid PostgreSQL identifier: {identifier}");
        }

        const int MaxIdentifierBytes = 63; // PostgreSQL NAMEDATALEN - 1; longer names are silently truncated.
        if (System.Text.Encoding.UTF8.GetByteCount(identifier) > MaxIdentifierBytes)
        {
            throw new System.ArgumentException(
                $"PostgreSQL identifier exceeds the {MaxIdentifierBytes}-byte limit and would be silently truncated.",
                parameterName);
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
