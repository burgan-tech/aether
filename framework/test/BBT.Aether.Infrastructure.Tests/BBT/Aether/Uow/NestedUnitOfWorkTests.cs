using System;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class NestedUnitOfWorkTests
{
    private sealed class TerminalProbeDbContext(DbContextOptions<TerminalProbeDbContext> options)
        : DbContext(options);

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

    [Fact]
    public async Task Incomplete_required_participant_is_not_active_after_shared_root_completes()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await outer.CommitAsync();

        inner.IsCompleted.ShouldBeFalse();
        manager.Current.ShouldBeNull();

        await inner.DisposeAsync();
        await outer.DisposeAsync();
    }

    [Fact]
    public async Task Incomplete_required_participant_is_not_active_after_shared_root_is_disposed()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await outer.DisposeAsync();

        inner.IsCompleted.ShouldBeFalse();
        inner.IsDisposed.ShouldBeFalse();
        manager.Current.ShouldBeNull();

        await inner.DisposeAsync();
        manager.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Completed_required_participant_cannot_later_abort_shared_root()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await using var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.CommitAsync();
        await inner.RollbackAsync();
        inner.Abort();
        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeTrue();
        outer.IsAborted.ShouldBeFalse();

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Rolled_back_required_participant_remains_terminal_on_later_commit_and_rollback()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await using var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.RollbackAsync();
        await inner.CommitAsync();
        await inner.RollbackAsync();

        inner.IsCompleted.ShouldBeTrue();
        outer.IsAborted.ShouldBeTrue();

        await outer.RollbackAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Disposed_required_participant_operations_do_not_mutate_shared_root()
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
        await inner.RollbackAsync();
        await inner.CommitAsync();
        inner.Abort();

        inner.IsDisposed.ShouldBeTrue();
        inner.IsCompleted.ShouldBeFalse();
        outer.IsAborted.ShouldBeFalse();
        outer.IsCompleted.ShouldBeFalse();

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Nontransactional_required_can_join_transactional_outer()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });
        await using var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        inner.Options.ShouldBeSameAs(outer.Options);
        inner.Options!.IsTransactional.ShouldBeTrue();

        await inner.CommitAsync();
        outer.IsCompleted.ShouldBeFalse();

        await outer.CommitAsync();
        outer.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Completed_required_participant_rejects_work_operations()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await using var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.CommitAsync();

        await AssertWorkOperationsAreRejectedAsync(inner);
        outer.IsAborted.ShouldBeFalse();
    }

    [Fact]
    public async Task Rolled_back_required_participant_rejects_work_operations()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await using var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await inner.RollbackAsync();

        await AssertWorkOperationsAreRejectedAsync(inner);
        outer.IsAborted.ShouldBeTrue();
    }

    [Fact]
    public async Task Disposed_required_participant_rejects_work_operations()
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

        await AssertWorkOperationsAreRejectedAsync(inner);
        outer.IsAborted.ShouldBeFalse();
    }

    [Fact]
    public async Task Incomplete_required_participant_cannot_work_or_abort_after_root_commit()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await outer.CommitAsync();
        await AssertWorkOperationsAreRejectedAsync(inner);
        await inner.RollbackAsync();
        inner.Abort();

        outer.IsAborted.ShouldBeFalse();

        await inner.DisposeAsync();
        await outer.DisposeAsync();
    }

    [Fact]
    public async Task Incomplete_required_participant_cannot_work_or_abort_after_root_disposal()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var inner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await outer.DisposeAsync();
        await AssertWorkOperationsAreRejectedAsync(inner);
        await inner.RollbackAsync();
        inner.Abort();

        outer.IsAborted.ShouldBeFalse();

        await inner.DisposeAsync();
    }

    private static async Task AssertWorkOperationsAreRejectedAsync(IUnitOfWork unitOfWork)
    {
        var contextException = await Should.ThrowAsync<InvalidOperationException>(() =>
            ((IEfCoreUnitOfWork)unitOfWork).GetDbContextAsync<TerminalProbeDbContext>("schema_a"));
        contextException.Message.ShouldContain("completed or disposed");

        var saveException = await Should.ThrowAsync<InvalidOperationException>(() =>
            unitOfWork.SaveChangesAsync());
        saveException.Message.ShouldContain("completed or disposed");
    }
}
