using System;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Represents the current schema context. The schema is an immutable working context
/// pushed/popped via nested disposable scopes (AsyncLocal). It is NOT mutated in place.
/// </summary>
public interface ICurrentSchema
{
    /// <summary>The current (top-of-stack) schema name, or null if no scope is active.</summary>
    string? Name { get; }

    /// <summary>
    /// Pushes a schema onto the current async-flow stack and returns a disposable that pops it.
    /// </summary>
    IDisposable Change(string schema);
}
