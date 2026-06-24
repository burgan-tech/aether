using System;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Minimal, single-threaded <see cref="ICurrentSchema"/> that exposes a fixed (re-pointable) schema name.
/// Intended for paths that run OUTSIDE the request/DI pipeline — e.g. a multi-schema migrator that builds
/// contexts manually and must force a known target schema — where the AsyncLocal-backed default
/// <see cref="CurrentSchema"/> (which isolates per async flow) is the wrong tool.
/// </summary>
/// <remarks>
/// Unlike <see cref="CurrentSchema"/> this holds a plain instance field, NOT an AsyncLocal: the schema must
/// stay visible across all of the caller's (possibly async) calls on the same instance, rather than being
/// scoped per async flow. It is therefore NOT safe to share one instance across concurrent flows — give each
/// migration worker its own instance. <see cref="Change"/> re-points the schema and restores the previous
/// value on dispose, covering the "set once per loop iteration" use case without a separate Set method.
/// </remarks>
public sealed class StaticCurrentSchema : ICurrentSchema
{
    private string? _name;

    /// <summary>Creates the accessor with an optional initial schema.</summary>
    public StaticCurrentSchema(string? schema = null) => _name = schema;

    /// <inheritdoc />
    public string? Name => _name;

    /// <inheritdoc />
    public IDisposable Change(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        var previous = _name;
        _name = schema;
        return new Restore(this, previous);
    }

    private sealed class Restore(StaticCurrentSchema owner, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._name = previous;
        }
    }
}
