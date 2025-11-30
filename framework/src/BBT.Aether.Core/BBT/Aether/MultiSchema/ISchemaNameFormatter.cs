namespace BBT.Aether.MultiSchema;

/// <summary>
/// Formats schema names according to database naming conventions.
/// </summary>
public interface ISchemaNameFormatter
{
    /// <summary>
    /// Formats the schema name to comply with database naming standards.
    /// </summary>
    /// <param name="schemaName">The raw schema name.</param>
    /// <returns>The formatted schema name.</returns>
    string Format(string schemaName);
}

