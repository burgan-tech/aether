using BBT.Aether.Events;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events;

public sealed class WorkerIdentityTests
{
    [Fact]
    public void Value_includes_app_name_and_pid()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("my-service");

        var identity = new WorkerIdentity(env);

        identity.Value.ShouldStartWith("my-service/");
        identity.Value.ShouldContain($"/{Environment.ProcessId}/");
    }

    [Fact]
    public void Two_instances_have_different_instance_ids()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("svc");

        var a = new WorkerIdentity(env);
        var b = new WorkerIdentity(env);

        a.Value.ShouldNotBe(b.Value);
    }

    [Fact]
    public void Value_has_four_segments()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("svc");

        var identity = new WorkerIdentity(env);

        identity.Value.Split('/').Length.ShouldBe(4);
    }
}
