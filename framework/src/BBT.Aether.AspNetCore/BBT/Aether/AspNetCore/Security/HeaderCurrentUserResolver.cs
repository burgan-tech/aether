using BBT.Aether.Users;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Security;

/// <summary>
/// Default implementation of <see cref="ICurrentUserResolver"/> that resolves user information from HTTP headers.
/// </summary>
public class HeaderCurrentUserResolver(IHttpContextAccessor httpContextAccessor) : ICurrentUserResolver
{
    public BasicUserInfo? GetCurrentUser()
    {
        var context = httpContextAccessor.HttpContext;
        if (context == null)
        {
            return null;
        }

        var request = context.Request;

        return new BasicUserInfo(
            request.GetClaimHeader(AetherClaimTypes.UserId) ?? string.Empty,
            request.GetClaimHeader(AetherClaimTypes.UserName) ?? string.Empty,
            request.GetClaimHeader(AetherClaimTypes.Name) ?? string.Empty,
            request.GetClaimHeader(AetherClaimTypes.SurName) ?? string.Empty,
            CurrentUserHeaderExtensions.ParseRolesFromHeader(request.GetClaimHeader(AetherClaimTypes.Role)) ?? [],
            request.GetClaimHeader(AetherClaimTypes.ActorUserId) ?? string.Empty,
            request.GetClaimHeader(AetherClaimTypes.ActorSub) ?? string.Empty,
            request.GetClaimHeader(AetherClaimTypes.ConsentId) ?? string.Empty,
            // Position stays null when the request carries none, unlike the fields above: it is a newer
            // claim with no empty-string callers to keep working, and null lets consumers fall through
            // with `?? fallback` instead of having to test for empty.
            request.GetClaimHeader(AetherClaimTypes.Position)
        );
    }
}
