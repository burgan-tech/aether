using System;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Default implementation of <see cref="ICurrentSchema"/>.
/// </summary>
public class CurrentSchema : ICurrentSchema
{
    private readonly ISchemaAccessor _accessor;
    private readonly ISchemaNameFormatter _formatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="CurrentSchema"/> class.
    /// </summary>
    /// <param name="accessor">The schema accessor.</param>
    /// <param name="formatter">The schema name formatter.</param>
    public CurrentSchema(ISchemaAccessor accessor, ISchemaNameFormatter formatter)
    {
        _accessor = accessor;
        _formatter = formatter;
    }

    /// <inheritdoc />
    public string? Name => _accessor.Schema;

    /// <inheritdoc />
    public bool IsResolved => !string.IsNullOrWhiteSpace(_accessor.Schema);

    /// <inheritdoc />
    public void Set(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema cannot be null or whitespace.", nameof(schema));

        // Format the schema name according to database naming conventions
        var formattedSchema = _formatter.Format(schema);
        
        _accessor.Schema = formattedSchema;
    }
}

