using System;

namespace BBT.Aether.Users;

/// <summary>
/// Represents the current user.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// Gets a value indicating whether the user is authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the user's ID.
    /// </summary>
    string? Id { get; }

    /// <summary>
    /// Gets the user's username.
    /// </summary>
    string? UserName { get; }

    /// <summary>
    /// Gets the user's name.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Gets the user's surname.
    /// </summary>
    string? Surname { get; }

    /// <summary>
    /// Gets the user's roles.
    /// </summary>
    string[]? Roles { get; }

    /// <summary>
    /// Gets the user's primary role — the first entry of <see cref="Roles"/>.
    /// <para>
    /// Provided for legacy systems that carry a single <c>role</c> claim: there the header holds one
    /// value, so this returns exactly that value. When a caller may carry several roles, use
    /// <see cref="Roles"/> — this property only ever reflects the first one.
    /// </para>
    /// </summary>
    string? Role { get; }

    /// <summary>
    /// Gets the user's position — the organizational posting that, together with the actor
    /// (<c>act_sub</c>) and subject (<c>sub</c>) identities, identifies the caller at an external
    /// identity provider.
    /// </summary>
    string? Position { get; }

    /// <summary>
    /// Gets the actor user's ID (in case of delegation).
    /// </summary>
    string? ActorUserId { get; }

    /// <summary>
    /// Gets the actor user's username (in case of delegation).
    /// </summary>
    string? ActorUserName { get; }

    /// <summary>
    /// Gets the consent ID.
    /// </summary>
    string? ConsentId { get; }

    /// <summary>
    /// Checks if the user is in the specified role.
    /// </summary>
    /// <param name="roleName">The name of the role to check.</param>
    /// <returns>True if the user is in the role, otherwise false.</returns>
    bool IsInRole(string roleName);

    /// <summary>
    /// Changes the current user's information within a disposable scope.
    /// </summary>
    /// <param name="id">The user's ID.</param>
    /// <param name="userName">The user's username.</param>
    /// <param name="name">The user's name.</param>
    /// <param name="surname">The user's surname.</param>
    /// <param name="roles">The user's roles.</param>
    /// <param name="actorUserId">The actor user's ID.</param>
    /// <param name="actorUserName">The actor user's username.</param>
    /// <param name="consentId">The consent ID.</param>
    /// <param name="position">The user's position (organizational posting).</param>
    /// <returns>An IDisposable that reverts the changes when disposed.</returns>
    IDisposable Change(
        string? id,
        string? userName = null,
        string? name = null,
        string? surname = null,
        string[]? roles = null,
        string? actorUserId = null,
        string? actorUserName = null,
        string? consentId = null,
        string? position = null);

    /// <summary>
    /// Changes the current user's information within a disposable scope.
    /// <para>
    /// Prefer this overload: it carries every field of <see cref="BasicUserInfo"/>, so a call site does
    /// not have to be revisited when a new field is added to the user model.
    /// </para>
    /// </summary>
    /// <param name="user">The user information to make current.</param>
    /// <returns>An IDisposable that reverts the change when disposed.</returns>
    IDisposable Change(BasicUserInfo user);
}