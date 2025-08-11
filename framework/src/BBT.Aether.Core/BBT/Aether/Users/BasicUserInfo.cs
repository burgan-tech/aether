namespace BBT.Aether.Users;

/// <summary>
/// Represents basic user information.
/// </summary>
public class BasicUserInfo(
    string? id,
    string? userName = null,
    string? name = null,
    string? surname = null,
    string[]? roles = null,
    string? actorUserId = null,
    string? actorUserName = null,
    string? consentId = null)
{
    /// <summary>
    /// Gets or sets the user's ID.
    /// </summary>
    public string? Id { get; set; } = id;
    /// <summary>
    /// Gets or sets the user's username.
    /// </summary>
    public string? UserName { get; set; } = userName;
    /// <summary>
    /// Gets or sets the user's name.
    /// </summary>
    public string? Name { get; set; } = name;
    /// <summary>
    /// Gets or sets the user's surname.
    /// </summary>
    public string? Surname { get; set; } = surname;
    /// <summary>
    /// Gets or sets the user's roles.
    /// </summary>
    public string[]? Roles { get; set; } = roles;
    /// <summary>
    /// Gets or sets the actor user's ID.
    /// </summary>
    public string? ActorUserId { get; set; } = actorUserId;
    /// <summary>
    /// Gets or sets the actor user's username.
    /// </summary>
    public string? ActorUserName { get; set; } = actorUserName;
    /// <summary>
    /// Gets or sets the consent ID.
    /// </summary>
    public string? ConsentId { get; set; } = consentId;
}