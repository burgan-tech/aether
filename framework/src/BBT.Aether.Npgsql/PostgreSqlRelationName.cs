using System;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Formats an EF Core table mapping as a validated, schema-qualified PostgreSQL relation name.
/// </summary>
public static class PostgreSqlRelationName
{
    /// <summary>
    /// Returns the mapped relation, substituting the runtime schema for schema-neutral or
    /// Aether-placeholder mappings while preserving explicit schema mappings.
    /// </summary>
    public static string For(IReadOnlyEntityType entityType, string runtimeSchema)
    {
        var table = entityType.GetTableName()
            ?? throw new InvalidOperationException($"Entity '{entityType.Name}' has no table mapping.");
        var mapped = entityType.GetSchema();
        var schema = string.IsNullOrWhiteSpace(mapped) || mapped == AetherSchemaModel.Placeholder
            ? runtimeSchema
            : mapped;

        return $"{PostgreSqlIdentifier.QuoteSchema(schema)}.{PostgreSqlIdentifier.QuoteTable(table)}";
    }
}
