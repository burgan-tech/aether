using System.Threading;

namespace BBT.Aether.MultiSchema;

/// <summary>
/// Provides an <see cref="ISchemaAccessor"/> implementation using <see cref="AsyncLocal{T}"/>.
/// This allows schema context to flow across async/await boundaries.
/// </summary>
public class AsyncLocalSchemaAccessor : ISchemaAccessor
{
    private readonly AsyncLocal<string?> _current;

    /// <summary>
    /// Gets the singleton instance of the accessor.
    /// </summary>
    public static AsyncLocalSchemaAccessor Instance { get; } = new();

    private AsyncLocalSchemaAccessor()
    {
        _current = new AsyncLocal<string?>();
    }

    /// <inheritdoc />
    public string? Schema
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

