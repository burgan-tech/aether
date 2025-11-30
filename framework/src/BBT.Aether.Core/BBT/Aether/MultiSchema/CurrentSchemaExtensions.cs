using System;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Extension methods for <see cref="ICurrentSchema"/>.
/// </summary>
public static class CurrentSchemaExtensions
{
    /// <summary>
    /// Temporarily sets the current schema and returns an <see cref="IDisposable"/>
    /// that restores the previous schema when disposed.
    /// </summary>
    /// <param name="currentSchema">The current schema instance.</param>
    /// <param name="schema">The schema to set temporarily.</param>
    /// <returns>An <see cref="IDisposable"/> that restores the previous schema.</returns>
    /// <example>
    /// <code>
    /// using (currentSchema.Use("runtime_loan"))
    /// {
    ///     // Operations within this schema context
    /// }
    /// // Previous schema is restored here
    /// </code>
    /// </example>
    public static IDisposable Use(this ICurrentSchema currentSchema, string schema)
    {
        var previous = currentSchema.Name;
        currentSchema.Set(schema);
        return new RestoreSchemaOnDispose(currentSchema, previous);
    }

    private sealed class RestoreSchemaOnDispose : IDisposable
    {
        private readonly ICurrentSchema _currentSchema;
        private readonly string? _previous;
        private bool _disposed;

        public RestoreSchemaOnDispose(ICurrentSchema currentSchema, string? previous)
        {
            _currentSchema = currentSchema;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            if (_previous is not null)
                _currentSchema.Set(_previous);
            
            _disposed = true;
        }
    }
}

