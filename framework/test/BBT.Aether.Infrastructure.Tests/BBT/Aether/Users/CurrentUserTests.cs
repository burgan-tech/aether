using System;
using BBT.Aether.TestSupport;
using BBT.Aether.Users;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Users;

public class CurrentUserTests
{
    private static ICurrentUser Create() => TestCurrentUserAccessor.NewCurrentUser();

    [Fact]
    public void Role_WhenNoUser_IsNull()
    {
        var user = Create();

        user.Role.ShouldBeNull();
        user.Position.ShouldBeNull();
        user.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void Role_WhenSingleRole_ReturnsThatRole()
    {
        var user = Create();

        using (user.Change("1", "12345678901", roles: ["maker"]))
        {
            user.Role.ShouldBe("maker");
            user.Roles.ShouldBe(new[] { "maker" });
        }
    }

    [Fact]
    public void Role_WhenMultipleRoles_ReturnsFirst()
    {
        var user = Create();

        using (user.Change("1", "12345678901", roles: ["maker", "checker"]))
        {
            user.Role.ShouldBe("maker");
        }
    }

    [Fact]
    public void Role_WhenRolesEmptyOrNull_IsNull()
    {
        var user = Create();

        using (user.Change("1", "12345678901", roles: []))
        {
            user.Role.ShouldBeNull();
        }

        using (user.Change("1", "12345678901"))
        {
            user.Role.ShouldBeNull();
        }
    }

    [Fact]
    public void Position_IsReadFromCurrentUserInfo()
    {
        var user = Create();

        using (user.Change("1", "12345678901", position: "branch-teller"))
        {
            user.Position.ShouldBe("branch-teller");
        }

        user.Position.ShouldBeNull();
    }

    [Fact]
    public void Change_WithBasicUserInfo_MakesEveryFieldCurrent()
    {
        var user = Create();
        var info = new BasicUserInfo(
            "42",
            "12345678901",
            "Ada",
            "Lovelace",
            ["maker", "checker"],
            "99",
            "10987654321",
            "consent-1",
            "branch-teller");

        using (user.Change(info))
        {
            user.IsAuthenticated.ShouldBeTrue();
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
            user.IsInRole("checker").ShouldBeTrue();
            user.IsInRole("approver").ShouldBeFalse();
        }

        user.Id.ShouldBeNull();
        user.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void Change_WhenNested_RestoresOuterUserOnDispose()
    {
        var user = Create();

        using (user.Change("outer", "111", roles: ["maker"], position: "hq"))
        {
            using (user.Change("inner", "222", roles: ["checker"], position: "branch"))
            {
                user.Id.ShouldBe("inner");
                user.Role.ShouldBe("checker");
                user.Position.ShouldBe("branch");
            }

            user.Id.ShouldBe("outer");
            user.Role.ShouldBe("maker");
            user.Position.ShouldBe("hq");
        }
    }

    [Fact]
    public void Change_WithNullUserInfo_Throws()
    {
        var user = Create();

        Should.Throw<ArgumentNullException>(() => user.Change(null!));
    }
}
