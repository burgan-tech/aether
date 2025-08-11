namespace BBT.Aether.Users;

/// <summary>
/// Defines the claim types used by Aether.
/// </summary>
public static class AetherClaimTypes
{
    /// <summary>
    /// Default: sub
    /// (Identity No)
    /// </summary>
    public static string UserName { get; set; } = "sub";
    
    /// <summary>
    /// Default: given_name
    /// </summary>
    public static string Name { get; set; } = "given_name";

    /// <summary>
    /// Default: family_name
    /// </summary>
    public static string SurName { get; set; } = "family_name";

    /// <summary>
    /// Default: userid
    /// </summary>
    public static string UserId { get; set; } = "userId";

    /// <summary>
    /// Default: role
    /// </summary>
    public static string Role { get; set; } = "role";

    /// <summary>
    /// Default: email
    /// </summary>
    public static string Email { get; set; } = "email";
    
    /// <summary>
    /// Default: phone_number
    /// </summary>
    public static string Phone { get; set; } = "phone_number";

    /// <summary>
    /// Default: act_sub
    /// (Actor Delegation) - sub
    /// </summary>
    public static string ActorSub { get; set; } = "act_sub";
    
    /// <summary>
    /// Default: act_uid
    /// (Actor Delegation) - userid
    /// </summary>
    public static string ActorUserId { get; set; } = "act_uid";
    
    /// <summary>
    /// Default: "client_id"
    /// </summary>
    public static string ClientId { get; set; } = "client_id";
    
    /// <summary>
    /// Default: "consent_id"
    /// </summary>
    public static string ConsentId { get; set; } = "consent_id";
}