using System.Threading.Tasks;
using BBT.Aether.Users;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Security;

public class AetherCurrentUserMiddleware(ICurrentUser currentUser, ICurrentUserResolver currentUserResolver)
    : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var basicUserInfo = currentUserResolver.GetCurrentUser();
        if (basicUserInfo == null)
        {
            await next(context);
            return;
        }

        using (currentUser.Change(basicUserInfo))
        {
            await next(context);
        }
    }
}
