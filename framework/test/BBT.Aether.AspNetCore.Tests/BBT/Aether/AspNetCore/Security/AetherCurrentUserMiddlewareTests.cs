using System.Threading.Tasks;
using BBT.Aether.AspNetCore.Security;
using BBT.Aether.Users;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.AspNetCore.Tests.BBT.Aether.AspNetCore.Security;

public class AetherCurrentUserMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenResolverReturnsNull_DoesNotChangeUserButStillCallsNext()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        var resolver = Substitute.For<ICurrentUserResolver>();
        resolver.GetCurrentUser().Returns((BasicUserInfo?)null);
        var nextCalled = false;

        await new AetherCurrentUserMiddleware(currentUser, resolver)
            .InvokeAsync(new DefaultHttpContext(), _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        nextCalled.ShouldBeTrue();
        currentUser.DidNotReceiveWithAnyArgs().Change(Arg.Any<BasicUserInfo>());
    }

    [Fact]
    public async Task InvokeAsync_MakesTheResolvedUserCurrentForTheRequestOnly()
    {
        var currentUser = new CurrentUser(new PerInstanceCurrentUserAccessor());
        var resolver = Substitute.For<ICurrentUserResolver>();
        resolver.GetCurrentUser().Returns(new BasicUserInfo(
            "42", "12345678901", "Ada", "Lovelace",
            ["maker", "checker"], "99", "10987654321", "consent-1", "branch-teller"));

        string? positionDuringRequest = null;
        string? roleDuringRequest = null;

        await new AetherCurrentUserMiddleware(currentUser, resolver)
            .InvokeAsync(new DefaultHttpContext(), _ =>
            {
                positionDuringRequest = currentUser.Position;
                roleDuringRequest = currentUser.Role;
                return Task.CompletedTask;
            });

        positionDuringRequest.ShouldBe("branch-teller");
        roleDuringRequest.ShouldBe("maker");
        currentUser.Position.ShouldBeNull();
        currentUser.IsAuthenticated.ShouldBeFalse();
    }

    private sealed class PerInstanceCurrentUserAccessor : ICurrentUserAccessor
    {
        public BasicUserInfo? Current { get; set; }
    }
}
