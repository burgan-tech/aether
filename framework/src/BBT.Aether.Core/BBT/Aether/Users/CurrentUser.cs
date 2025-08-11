using System;
using System.Linq;

namespace BBT.Aether.Users;

/// <summary>
/// Default implementation of <see cref="ICurrentUser"/>.
/// </summary>
public class CurrentUser(ICurrentUserAccessor currentUserAccessor) : ICurrentUser
{
    /// <inheritdoc />
    public bool IsAuthenticated => !UserName.IsNullOrEmpty();
    /// <inheritdoc />
    public string? Id => currentUserAccessor.Current?.Id;
    /// <inheritdoc />
    public string? UserName => currentUserAccessor.Current?.UserName;
    /// <inheritdoc />
    public string? Name => currentUserAccessor.Current?.Name;
    /// <inheritdoc />
    public string? Surname => currentUserAccessor.Current?.Surname;
    /// <inheritdoc />
    public string[]? Roles => currentUserAccessor.Current?.Roles;
    /// <inheritdoc />
    public string? ActorUserId => currentUserAccessor.Current?.ActorUserId; 
    /// <inheritdoc />
    public string? ActorUserName => currentUserAccessor.Current?.ActorUserName; 
    /// <inheritdoc />
    public string? ConsentId => currentUserAccessor.Current?.ConsentId; 

    /// <inheritdoc />
    public bool IsInRole(string roleName)
    {
        return Roles?.Any(a => a == roleName) ?? false;
    }
    

    /// <inheritdoc />
    public IDisposable Change(
        string? id,
        string? userName = null,
        string? name = null,
        string? surname = null,
        string[]? roles = null,
        string? actorUserId = null,
        string? actorUserName = null,
        string? consentId = null
    )
    {
        return SetCurrent(id, userName, name, surname, roles, actorUserId, actorUserName, consentId);
    }

    private IDisposable SetCurrent(
        string? id,
        string? userName = null,
        string? name = null,
        string? surname = null,
        string[]? roles = null,
        string? actorUserId = null,
        string? actorUserName = null,
        string? consentId = null
    )
    {
        var parentScope = currentUserAccessor.Current;
        currentUserAccessor.Current = new BasicUserInfo(id, userName, name, surname, roles, actorUserId, actorUserName, consentId);
        return new DisposeAction(() => { currentUserAccessor.Current = parentScope; });
    }
}