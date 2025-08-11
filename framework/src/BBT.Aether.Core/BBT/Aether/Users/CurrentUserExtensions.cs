using System.Diagnostics;

namespace BBT.Aether.Users;

/// <summary>
/// Extension methods for <see cref="ICurrentUser"/>.
/// </summary>
public static class CurrentUserExtensions
{
    /// <summary>
    /// Gets the username of the current user.
    /// </summary>
    /// <param name="currentUser">The current user.</param>
    /// <returns>The username of the current user.</returns>
    public static string GetUserName(this ICurrentUser currentUser)
    {
        Debug.Assert(currentUser.UserName != null, "currentUser.UserName != null");

        return currentUser!.UserName!;
    }
}