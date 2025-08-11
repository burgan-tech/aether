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
    /// <returns>An IDisposable that reverts the changes when disposed.</returns>
    IDisposable Change(
        string? id,
        string? userName = null,
        string? name = null,
        string? surname = null,
        string[]? roles = null,
        string? actorUserId = null,
        string? actorUserName = null,
        string? consentId = null);
}