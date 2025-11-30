using System.Linq;
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

        var userId = context.Request.Headers[AetherClaimTypes.UserId].FirstOrDefault() ?? string.Empty;
        var userName = context.Request.Headers[AetherClaimTypes.UserName].FirstOrDefault() ?? string.Empty;
        var name = context.Request.Headers[AetherClaimTypes.Name].FirstOrDefault() ?? string.Empty;
        var surname = context.Request.Headers[AetherClaimTypes.SurName].FirstOrDefault() ?? string.Empty;
        var rolesHeader = context.Request.Headers[AetherClaimTypes.Role].FirstOrDefault();
        var roles = rolesHeader != null ? rolesHeader.Split(',') : [];
        var actorUserName = context.Request.Headers[AetherClaimTypes.ActorSub].FirstOrDefault() ?? string.Empty;
        var consentId = context.Request.Headers[AetherClaimTypes.ConsentId].FirstOrDefault() ?? string.Empty;
        var actorUserId = context.Request.Headers[AetherClaimTypes.ActorUserId].FirstOrDefault() ?? string.Empty;
        
        return new BasicUserInfo(
            userId,
            userName,
            name,
            surname,
            roles,
            actorUserId,
            actorUserName,
            consentId
        );
    }
}