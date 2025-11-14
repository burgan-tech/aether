namespace BBT.Aether.Users;

/// <summary>
/// Interface for resolving current user information from the request context.
/// Developers can implement this interface to provide custom user resolution logic.
/// </summary>
public interface ICurrentUserResolver
{
    /// <summary>
    /// Resolves the current user information from the request context.
    /// </summary>
    /// <returns>The basic user information if available; otherwise, null.</returns>
    BasicUserInfo? GetCurrentUser();
}

