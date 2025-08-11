using System.Threading;

namespace BBT.Aether.Users;

/// <summary>
/// Provides an <see cref="ICurrentUserAccessor"/> implementation using <see cref="AsyncLocal{T}"/>.
/// </summary>
public class AsyncLocalCurrentUserAccessor : ICurrentUserAccessor
{
    /// <summary>
    /// Gets the singleton instance of the accessor.
    /// </summary>
    public static AsyncLocalCurrentUserAccessor Instance { get; } = new();

    /// <inheritdoc />
    public BasicUserInfo? Current {
        get => _currentScope.Value;
        set => _currentScope.Value = value;
    }

    private readonly AsyncLocal<BasicUserInfo?> _currentScope;

    private AsyncLocalCurrentUserAccessor()
    {
        _currentScope = new AsyncLocal<BasicUserInfo?>();
    }
}