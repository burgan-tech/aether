namespace BBT.Aether.Users;

/// <summary>
/// Provides access to the current user's basic information.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>
    /// Gets or sets the current user's basic information.
    /// </summary>
    BasicUserInfo? Current { get; set; }
}