using System;
using System.Collections.Generic;
using System.Threading;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Default <see cref="ICurrentSchema"/> backed by an AsyncLocal stack so nested
/// schema scopes flow across async calls and restore the previous schema on dispose.
/// </summary>
public sealed class CurrentSchema(ISchemaNameFormatter formatter) : ICurrentSchema
{
    private static readonly AsyncLocal<Stack<string>?> Current = new();

    public string? Name => Current.Value is { Count: > 0 } s ? s.Peek() : null;

    public IDisposable Change(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        var formatted = formatter.Format(schema);
        Current.Value ??= new Stack<string>();
        Current.Value.Push(formatted);

        return new PopOnDispose(formatted);
    }

    private sealed class PopOnDispose(string expected) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var popped = Current.Value?.Count > 0 ? Current.Value.Pop() : null;
            if (!string.Equals(popped, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Schema scope corrupted: out-of-order disposal detected.");
            }
        }
    }
}
