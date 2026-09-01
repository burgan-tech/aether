using System.Collections.Generic;
using BBT.Aether.AspNetCore.Security;
using BBT.Aether.Users;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.AspNetCore.Tests.BBT.Aether.AspNetCore.Security;

public class HeaderCurrentUserResolverTests
{
    [Fact]
    public void GetCurrentUser_WhenNoHttpContext_ReturnsNull()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        new HeaderCurrentUserResolver(accessor).GetCurrentUser().ShouldBeNull();
    }

    [Fact]
    public void GetCurrentUser_MapsEveryClaimHeader()
    {
        var resolver = ResolverFor(new Dictionary<string, string>
        {
            [AetherClaimTypes.UserId] = "42",
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.Name] = "Ada",
            [AetherClaimTypes.SurName] = "Lovelace",
            [AetherClaimTypes.Role] = "maker,checker",
            [AetherClaimTypes.ActorUserId] = "99",
            [AetherClaimTypes.ActorSub] = "10987654321",
            [AetherClaimTypes.ConsentId] = "consent-1",
            [AetherClaimTypes.Position] = "branch-teller"
        });

        var user = resolver.GetCurrentUser();

        user.ShouldNotBeNull();
        user.Id.ShouldBe("42");
        user.UserName.ShouldBe("12345678901");
        user.Name.ShouldBe("Ada");
        user.Surname.ShouldBe("Lovelace");
        user.Roles.ShouldBe(new[] { "maker", "checker" });
        user.ActorUserId.ShouldBe("99");
        user.ActorUserName.ShouldBe("10987654321");
        user.ConsentId.ShouldBe("consent-1");
        user.Position.ShouldBe("branch-teller");
    }

    [Fact]
    public void GetCurrentUser_WhenRoleHeaderIsSpaceSeparated_SplitsIntoRoles()
    {
        var user = ResolverFor(new Dictionary<string, string>
        {
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.Role] = "maker checker"
        }).GetCurrentUser();

        user!.Roles.ShouldBe(new[] { "maker", "checker" });
    }

    [Fact]
    public void GetCurrentUser_WhenSingleLegacyRoleHeader_YieldsThatOneRole()
    {
        var user = ResolverFor(new Dictionary<string, string>
        {
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.Role] = " maker "
        }).GetCurrentUser();

        user!.Roles.ShouldBe(new[] { "maker" });
    }

    [Fact]
    public void GetCurrentUser_WhenNoRoleHeader_YieldsEmptyRoles()
    {
        var user = ResolverFor(new Dictionary<string, string>
        {
            [AetherClaimTypes.UserName] = "12345678901"
        }).GetCurrentUser();

        user!.Roles.ShouldBeEmpty();
        user.Position.ShouldBeNull();
    }

    private static HeaderCurrentUserResolver ResolverFor(Dictionary<string, string> headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (key, value) in headers)
        {
            context.Request.Headers[key] = value;
        }

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new HeaderCurrentUserResolver(accessor);
    }
}
