using System;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class NestedUnitOfWorkTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();
        services.AddScoped<IUnitOfWorkManager, UnitOfWorkManager>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Inner_required_commit_does_not_complete_root()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        inner.IsCompleted.ShouldBeFalse();
        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeTrue();
        outer.IsCompleted.ShouldBeFalse();

        await inner.DisposeAsync();
        manager.Current.ShouldBeSameAs(outer);

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Inner_required_rollback_aborts_root_and_failed_handlers_run_on_root_rollback()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var failedHandlerCalls = 0;
        using var subscription = outer.OnFailed((_, _) =>
        {
            failedHandlerCalls++;
            return Task.CompletedTask;
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.RollbackAsync();

        inner.IsCompleted.ShouldBeTrue();
        outer.IsAborted.ShouldBeTrue();
        outer.IsCompleted.ShouldBeFalse();
        failedHandlerCalls.ShouldBe(0);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => outer.CommitAsync());
        exception.Message.ShouldContain("aborted by an inner scope");

        await inner.DisposeAsync();
        manager.Current.ShouldBeSameAs(outer);

        await outer.RollbackAsync();
        outer.IsCompleted.ShouldBeTrue();
        failedHandlerCalls.ShouldBe(1);

        await outer.RollbackAsync();
        failedHandlerCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_incomplete_required_participant_restores_ambient_without_aborting_root()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.DisposeAsync();

        inner.IsDisposed.ShouldBeTrue();
        inner.IsCompleted.ShouldBeFalse();
        outer.IsAborted.ShouldBeFalse();
        outer.IsCompleted.ShouldBeFalse();
        manager.Current.ShouldBeSameAs(outer);

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Transactional_required_cannot_join_nontransactional_outer_with_Begin()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        var exception = Should.Throw<InvalidOperationException>(() => manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = true
        }));

        exception.Message.ShouldContain("UnitOfWorkScopeOption.RequiresNew");
        manager.Current.ShouldBeSameAs(outer);
        outer.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Transactional_required_cannot_join_nontransactional_outer_with_BeginAsync()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => manager.BeginAsync(
            new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.Required,
                IsTransactional = true
            }));

        exception.Message.ShouldContain("UnitOfWorkScopeOption.RequiresNew");
        manager.Current.ShouldBeSameAs(outer);
        outer.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task BeginAsync_required_commit_is_logical_only()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await using var inner = await manager.BeginAsync(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeTrue();
        outer.IsCompleted.ShouldBeFalse();
        manager.Current.ShouldBeSameAs(outer);

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task RequiresNew_inner_commit_still_completes_its_own_root_and_restores_outer()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeTrue();
        outer.IsCompleted.ShouldBeFalse();

        await inner.DisposeAsync();
        manager.Current.ShouldBeSameAs(outer);

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }
}
