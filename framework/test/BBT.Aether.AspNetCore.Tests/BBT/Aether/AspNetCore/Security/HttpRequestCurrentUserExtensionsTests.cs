using System.Collections.Generic;
using BBT.Aether.AspNetCore.Security;
using BBT.Aether.Users;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace BBT.Aether.AspNetCore.Tests.BBT.Aether.AspNetCore.Security;

public class HttpRequestCurrentUserExtensionsTests
{
    [Fact]
    public void GetClaimHeader_WhenAbsentOrEmpty_ReturnsNull()
    {
        var request = RequestWith(new Dictionary<string, string>
        {
            [AetherClaimTypes.Position] = string.Empty
        });

        request.GetClaimHeader(AetherClaimTypes.Position).ShouldBeNull();
        request.GetClaimHeader(AetherClaimTypes.UserName).ShouldBeNull();
    }

    [Fact]
    public void GetClaimHeader_ReturnsTheHeaderValue()
    {
        var request = RequestWith(new Dictionary<string, string>
        {
            [AetherClaimTypes.Position] = "branch-teller"
        });

        request.GetClaimHeader(AetherClaimTypes.Position).ShouldBe("branch-teller");
    }

    [Fact]
    public void GetCurrentUserHeaders_CollectsOnlyThePresentClaimHeaders()
    {
        var request = RequestWith(new Dictionary<string, string>
        {
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.Role] = "maker,checker",
            [AetherClaimTypes.Position] = "branch-teller",
            ["X-Unrelated"] = "ignored"
        });

        var headers = request.GetCurrentUserHeaders();

        headers.Keys.ShouldBe(
            [AetherClaimTypes.UserName, AetherClaimTypes.Role, AetherClaimTypes.Position],
            ignoreOrder: true);
        headers[AetherClaimTypes.Position].ShouldBe("branch-teller");
    }

    [Fact]
    public void GetCurrentUserHeaders_FeedsChangeFromHeaders()
    {
        var request = RequestWith(new Dictionary<string, string>
        {
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.Role] = "maker checker",
            [AetherClaimTypes.Position] = "branch-teller"
        });
        var currentUser = new CurrentUser(new PerInstanceCurrentUserAccessor());

        using (currentUser.ChangeFromHeaders(request.GetCurrentUserHeaders()))
        {
            currentUser.UserName.ShouldBe("12345678901");
            currentUser.Role.ShouldBe("maker");
            currentUser.Roles.ShouldBe(new[] { "maker", "checker" });
            currentUser.Position.ShouldBe("branch-teller");
        }

        currentUser.Position.ShouldBeNull();
    }

    private static HttpRequest RequestWith(Dictionary<string, string> headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (key, value) in headers)
        {
            context.Request.Headers[key] = value;
        }

        return context.Request;
    }

    private sealed class PerInstanceCurrentUserAccessor : ICurrentUserAccessor
    {
        public BasicUserInfo? Current { get; set; }
    }
}
