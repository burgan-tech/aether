using System.Collections.Generic;
using BBT.Aether.TestSupport;
using BBT.Aether.Users;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Users;

public class CurrentUserHeaderExtensionsTests
{
    [Fact]
    public void ChangeFromHeaders_WhenHeadersNull_DoesNotChangeUser()
    {
        var currentUser = Substitute.For<ICurrentUser>();

        using (currentUser.ChangeFromHeaders(null))
        {
        }

        currentUser.DidNotReceiveWithAnyArgs().Change(Arg.Any<BasicUserInfo>());
    }

    [Fact]
    public void ChangeFromHeaders_WhenHeadersEmpty_DoesNotChangeUser()
    {
        var currentUser = Substitute.For<ICurrentUser>();

        using (currentUser.ChangeFromHeaders(new Dictionary<string, string?>()))
        {
        }

        currentUser.DidNotReceiveWithAnyArgs().Change(Arg.Any<BasicUserInfo>());
    }

    [Fact]
    public void ChangeFromHeaders_MapsEveryClaimHeader()
    {
        var user = CreateCurrentUser();
        var headers = new Dictionary<string, string?>
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
        };

        using (user.ChangeFromHeaders(headers))
        {
            user.Id.ShouldBe("42");
            user.UserName.ShouldBe("12345678901");
            user.Name.ShouldBe("Ada");
            user.Surname.ShouldBe("Lovelace");
            user.Roles.ShouldBe(new[] { "maker", "checker" });
            user.Role.ShouldBe("maker");
            user.ActorUserId.ShouldBe("99");
            user.ActorUserName.ShouldBe("10987654321");
            user.ConsentId.ShouldBe("consent-1");
            user.Position.ShouldBe("branch-teller");
        }

        user.UserName.ShouldBeNull();
    }

    [Fact]
    public void ChangeFromHeaders_WhenOnlySubAndActSubPresent_LeavesTheRestNull()
    {
        var user = CreateCurrentUser();
        var headers = new Dictionary<string, string?>
        {
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.ActorSub] = "10987654321"
        };

        using (user.ChangeFromHeaders(headers))
        {
            user.UserName.ShouldBe("12345678901");
            user.ActorUserName.ShouldBe("10987654321");
            user.Id.ShouldBeNull();
            user.Roles.ShouldBeNull();
            user.Role.ShouldBeNull();
            user.Position.ShouldBeNull();
        }
    }

    [Fact]
    public void ChangeFromHeaders_WhenRoleHeaderEmpty_LeavesRolesNull()
    {
        var user = CreateCurrentUser();
        var headers = new Dictionary<string, string?>
        {
            [AetherClaimTypes.UserName] = "12345678901",
            [AetherClaimTypes.Role] = "   "
        };

        using (user.ChangeFromHeaders(headers))
        {
            user.Roles.ShouldBeNull();
            user.Role.ShouldBeNull();
        }
    }

    [Fact]
    public void ToForwardHeaders_IncludesPositionAndJoinsRoles()
    {
        var user = CreateCurrentUser();

        using (user.Change(new BasicUserInfo(
                   "42", "12345678901", "Ada", "Lovelace",
                   ["maker", "checker"], "99", "10987654321", "consent-1", "branch-teller")))
        {
            var headers = user.ToForwardHeaders();

            headers[AetherClaimTypes.UserId].ShouldBe("42");
            headers[AetherClaimTypes.UserName].ShouldBe("12345678901");
            headers[AetherClaimTypes.Name].ShouldBe("Ada");
            headers[AetherClaimTypes.SurName].ShouldBe("Lovelace");
            headers[AetherClaimTypes.Role].ShouldBe("maker,checker");
            headers[AetherClaimTypes.ActorUserId].ShouldBe("99");
            headers[AetherClaimTypes.ActorSub].ShouldBe("10987654321");
            headers[AetherClaimTypes.ConsentId].ShouldBe("consent-1");
            headers[AetherClaimTypes.Position].ShouldBe("branch-teller");
        }
    }

    [Fact]
    public void ToForwardHeaders_OmitsEmptyValues()
    {
        var user = CreateCurrentUser();

        using (user.Change("42", "12345678901", roles: []))
        {
            var headers = user.ToForwardHeaders();

            headers.Keys.ShouldBe([AetherClaimTypes.UserId, AetherClaimTypes.UserName], ignoreOrder: true);
            headers.ContainsKey(AetherClaimTypes.Position).ShouldBeFalse();
            headers.ContainsKey(AetherClaimTypes.Role).ShouldBeFalse();
        }
    }

    [Fact]
    public void ToForwardHeaders_IsCaseInsensitive()
    {
        var user = CreateCurrentUser();

        using (user.Change("42", "12345678901", position: "branch-teller"))
        {
            var headers = user.ToForwardHeaders();

            headers["POSITION"].ShouldBe("branch-teller");
        }
    }

    [Fact]
    public void ToForwardHeaders_RoundTripsThroughChangeFromHeaders()
    {
        var source = CreateCurrentUser();
        var target = CreateCurrentUser();

        using (source.Change(new BasicUserInfo(
                   "42", "12345678901", "Ada", "Lovelace",
                   ["maker", "checker"], "99", "10987654321", "consent-1", "branch-teller")))
        {
            var headers = source.ToForwardHeaders();

            using (target.ChangeFromHeaders(headers))
            {
                target.Id.ShouldBe(source.Id);
                target.UserName.ShouldBe(source.UserName);
                target.Name.ShouldBe(source.Name);
                target.Surname.ShouldBe(source.Surname);
                target.Roles.ShouldBe(source.Roles);
                target.ActorUserId.ShouldBe(source.ActorUserId);
                target.ActorUserName.ShouldBe(source.ActorUserName);
                target.ConsentId.ShouldBe(source.ConsentId);
                target.Position.ShouldBe(source.Position);
            }
        }
    }

    [Theory]
    [InlineData("maker,checker")]
    [InlineData("maker checker")]
    [InlineData("maker , checker")]
    [InlineData("  maker  checker  ")]
    public void ParseRolesFromHeader_SplitsOnCommaAndSpaceAndTrims(string value)
    {
        CurrentUserHeaderExtensions.ParseRolesFromHeader(value)
            .ShouldBe(new[] { "maker", "checker" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void ParseRolesFromHeader_WhenNoRole_ReturnsNull(string? value)
    {
        CurrentUserHeaderExtensions.ParseRolesFromHeader(value).ShouldBeNull();
    }

    private static ICurrentUser CreateCurrentUser() => TestCurrentUserAccessor.NewCurrentUser();
}
