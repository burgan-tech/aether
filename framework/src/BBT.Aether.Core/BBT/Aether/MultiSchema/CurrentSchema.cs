using System;
using System.Collections.Immutable;
using System.Threading;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Default <see cref="ICurrentSchema"/> backed by an AsyncLocal of an IMMUTABLE stack so nested
/// schema scopes flow across async calls and restore the previous schema on dispose.
/// </summary>
/// <remarks>
/// Every <see cref="Change"/>/dispose REASSIGNS <c>Current.Value</c> with a new immutable stack
/// (copy-on-write). This is what makes scopes isolated across async boundaries: AsyncLocal copies
/// the reference into child flows, so mutating a shared mutable stack in place would leak pushes/pops
/// between parallel branches and the parent. Reassigning a new immutable value keeps each flow's view
/// private.
/// </remarks>
public sealed class CurrentSchema(ISchemaNameFormatter formatter) : ICurrentSchema
{
    private static readonly AsyncLocal<ImmutableStack<string>?> Current = new();

    public string? Name => Current.Value is { IsEmpty: false } s ? s.Peek() : null;

    public IDisposable Change(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        var formatted = formatter.Format(schema);
        var previous = Current.Value ?? ImmutableStack<string>.Empty;
        Current.Value = previous.Push(formatted);

        return new RestoreOnDispose(previous, formatted);
    }

    private sealed class RestoreOnDispose(ImmutableStack<string> previous, string expected) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var current = Current.Value;
            if (current is null || current.IsEmpty || !string.Equals(current.Peek(), expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Schema scope corrupted: out-of-order disposal detected.");
            }

            Current.Value = previous;
        }
    }
}
