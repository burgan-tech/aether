using BBT.Aether.Users;

namespace BBT.Aether.TestSupport;

/// <summary>
/// A per-instance <see cref="ICurrentUserAccessor"/> for unit tests. The production
/// <see cref="AsyncLocalCurrentUserAccessor"/> is a singleton whose AsyncLocal value leaks between tests
/// that share a thread pool thread, which makes assertions on the ambient user order-dependent.
/// </summary>
public sealed class TestCurrentUserAccessor : ICurrentUserAccessor
{
    /// <inheritdoc />
    public BasicUserInfo? Current { get; set; }

    /// <summary>
    /// Creates an <see cref="ICurrentUser"/> backed by a fresh accessor.
    /// </summary>
    public static ICurrentUser NewCurrentUser() => new CurrentUser(new TestCurrentUserAccessor());
}
