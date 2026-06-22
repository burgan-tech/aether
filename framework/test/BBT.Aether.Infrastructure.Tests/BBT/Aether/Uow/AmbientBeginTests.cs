using System;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

/// <summary>
/// Verifies ambient (AsyncLocal) propagation semantics of the synchronous <see cref="IUnitOfWorkManager.Begin"/>
/// versus the asynchronous <see cref="IUnitOfWorkManager.BeginAsync"/>. No DbContext is registered: these tests
/// only exercise scope creation + ambient assignment, so the lazy connection is never opened.
/// </summary>
public sealed class AmbientBeginTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();
        services.AddScoped<IUnitOfWorkManager, UnitOfWorkManager>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Begin_sets_ambient_in_caller_flow()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        await using var uow = manager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        manager.Current.ShouldNotBeNull();
        manager.Current!.Id.ShouldBe(uow.Id);
    }

    [Fact]
    public async Task BeginAsync_does_not_propagate_ambient_to_caller()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        // Documents the known limitation: BeginAsync's ambient (AsyncLocal) write happens inside the
        // async state machine and does NOT flow back to the caller's execution context.
        await using var uow = await manager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        manager.Current.ShouldBeNull();
    }
}
