namespace BBT.Aether.MultiSchema;

/// <summary>
/// Provides access to the current schema storage mechanism.
/// </summary>
public interface ISchemaAccessor
{
    /// <summary>
    /// Gets or sets the current schema.
    /// </summary>
    string? Schema { get; set; }
}

