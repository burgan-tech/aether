using System.Text.RegularExpressions;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Validates and quotes PostgreSQL schema identifiers. Schema names cannot be passed
/// as SQL parameters, so they must be validated before interpolation into SET LOCAL.
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
    public static string QuoteSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || !ValidIdentifier.IsMatch(schema))
        {
            throw new System.InvalidOperationException($"Invalid PostgreSQL schema name: {schema}");
        }

        return "\"" + schema.Replace("\"", "\"\"") + "\"";
    }
}
