using System;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Represents the current schema context for multi-tenant or multi-schema scenarios.
/// </summary>
public interface ICurrentSchema
{
    /// <summary>
    /// Gets the current schema name (e.g., runtime_loan, app_audit, etc.).
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Gets a value indicating whether the schema has been resolved.
    /// </summary>
    bool IsResolved { get; }

    /// <summary>
    /// Sets the current schema manually.
    /// </summary>
    /// <param name="schema">The schema name to set.</param>
    /// <exception cref="ArgumentException">Thrown when schema is null or whitespace.</exception>
    void Set(string schema);
}

