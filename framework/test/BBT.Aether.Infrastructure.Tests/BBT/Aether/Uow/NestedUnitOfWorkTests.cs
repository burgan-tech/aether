using System;
using System.Reflection;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Aether.Aspects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class NestedUnitOfWorkTests
{
    private sealed class ThrowingAfterUnitOfWorkAttribute : UnitOfWorkAttribute
    {
        public Task ExecuteAsync(IUnitOfWork unitOfWork) =>
            ExecuteWithinUnitOfWorkAsync(unitOfWork, () => Task.CompletedTask, null!, default);

        protected override Task OnAfterAsync(PostSharp.Aspects.MethodInterceptionArgs args) =>
            Task.FromException(new InvalidOperationException("after failed"));
    }
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
    public async Task Required_aspect_after_hook_failure_aborts_shared_root_before_participant_completion()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var outer = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await using var participant = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = false
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ThrowingAfterUnitOfWorkAttribute().ExecuteAsync(participant));

        outer.IsAborted.ShouldBeTrue();
        participant.IsCompleted.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => outer.CommitAsync());
    }

    [Fact]
    public async Task UnitOfWorkScope_preserves_public_root_getter_only_for_owning_scope()
    {
        var property = typeof(UnitOfWorkScope).GetProperty(
            "Root", BindingFlags.Instance | BindingFlags.Public);
        property.ShouldNotBeNull();
        property.GetMethod.ShouldNotBeNull();

        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var owner = (UnitOfWorkScope)manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew
        });
        await using var participant = (UnitOfWorkScope)manager.Begin();

#pragma warning disable CS0618
        owner.Root.ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => _ = participant.Root)
            .Message.ShouldContain("owning");
#pragma warning restore CS0618
    }

    [Fact]
    public async Task Required_compatibility_uses_transaction_mode_captured_when_root_begins()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var originalOptions = new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        };
        await using var owner = manager.Begin(originalOptions);

        originalOptions.IsTransactional = true;
        owner.Options!.IsTransactional = true;

        Should.Throw<InvalidOperationException>(() => manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.Required,
            IsTransactional = true
        }));
    }

    [Fact]
    public async Task Terminal_composite_and_scope_reject_work_and_outer_mutation()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var owner = (UnitOfWorkScope)manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew
        });
#pragma warning disable CS0618
        var root = owner.Root;
#pragma warning restore CS0618

        await owner.CommitAsync();

        await AssertWorkOperationsAreRejectedAsync(root);
        Should.Throw<InvalidOperationException>(() => root.SetOuter(null));
        Should.Throw<InvalidOperationException>(() => owner.SetOuter(null));

        await owner.DisposeAsync();

        Should.Throw<InvalidOperationException>(() => root.SetOuter(null));
        Should.Throw<InvalidOperationException>(() => owner.SetOuter(null));
    }

    [Fact]
    public async Task SetOuter_rejects_self_reference_and_cyclic_outer_chains()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var owner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        Should.Throw<InvalidOperationException>(() => owner.SetOuter(owner));

        var cycleA = Substitute.For<IUnitOfWork>();
        var cycleB = Substitute.For<IUnitOfWork>();
        cycleA.Outer.Returns(cycleB);
        cycleB.Outer.Returns(cycleA);

        Should.Throw<InvalidOperationException>(() => owner.SetOuter(cycleA));

        var rootA = new CompositeUnitOfWork(serviceScope.ServiceProvider);
        var rootB = new CompositeUnitOfWork(serviceScope.ServiceProvider);
        rootA.SetOuter(rootB);

        Should.Throw<InvalidOperationException>(() => rootB.SetOuter(rootA));
    }

    [Fact]
    public async Task Completed_rolled_back_and_disposed_participants_reject_handler_registration()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var owner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        await using var completed = manager.Begin();
        await completed.CommitAsync();
        AssertHandlerRegistrationIsRejected(completed);

        await completed.DisposeAsync();
        await using var rolledBack = manager.Begin();
        await rolledBack.RollbackAsync();
        AssertHandlerRegistrationIsRejected(rolledBack);

        await rolledBack.DisposeAsync();
        var disposed = manager.Begin();
        await disposed.DisposeAsync();
        AssertHandlerRegistrationIsRejected(disposed);
    }

    [Fact]
    public async Task Root_terminal_participant_and_owner_reject_handler_registration()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var owner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        var participant = manager.Begin();

        await owner.CommitAsync();

        AssertHandlerRegistrationIsRejected(participant);
        AssertHandlerRegistrationIsRejected(owner);

        await participant.DisposeAsync();
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task Rolled_back_and_disposed_owners_reject_handler_registration()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        var rolledBack = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await rolledBack.RollbackAsync();
        AssertHandlerRegistrationIsRejected(rolledBack);
        await rolledBack.DisposeAsync();

        var disposed = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });
        await disposed.DisposeAsync();
        AssertHandlerRegistrationIsRejected(disposed);
    }

    [Fact]
    public async Task Terminal_composite_root_rejects_handler_registration()
    {
        await using var serviceScope = BuildProvider().CreateAsyncScope();
        var root = new CompositeUnitOfWork(serviceScope.ServiceProvider);
        root.InitializeCore(new UnitOfWorkOptions());

        await root.CommitAsync();

        AssertHandlerRegistrationIsRejected(root);
        await root.DisposeAsync();
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

    [Fact]
    public async Task Incomplete_required_participant_commit_is_no_op_after_root_commit()
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
        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeFalse();
        outer.IsCompleted.ShouldBeTrue();
        outer.IsAborted.ShouldBeFalse();

        await inner.DisposeAsync();
        await outer.DisposeAsync();
    }

    [Fact]
    public async Task Incomplete_required_participant_commit_is_no_op_after_root_rollback()
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

        await outer.RollbackAsync();
        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeFalse();
        outer.IsCompleted.ShouldBeTrue();
        outer.IsAborted.ShouldBeFalse();

        await inner.DisposeAsync();
        await outer.DisposeAsync();
    }

    [Fact]
    public async Task Incomplete_required_participant_commit_is_no_op_after_root_disposal()
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
        await inner.CommitAsync();

        inner.IsCompleted.ShouldBeFalse();
        outer.IsCompleted.ShouldBeTrue();
        outer.IsAborted.ShouldBeFalse();

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Owner_abort_is_no_op_after_commit()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var owner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        await owner.CommitAsync();
        owner.Abort();

        owner.IsCompleted.ShouldBeTrue();
        owner.IsAborted.ShouldBeFalse();

        await owner.DisposeAsync();
    }

    [Fact]
    public async Task Owner_abort_is_no_op_after_rollback()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var owner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        await owner.RollbackAsync();
        owner.Abort();

        owner.IsCompleted.ShouldBeTrue();
        owner.IsAborted.ShouldBeFalse();

        await owner.DisposeAsync();
    }

    [Fact]
    public async Task Owner_abort_is_no_op_after_disposal()
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var owner = manager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        await owner.DisposeAsync();
        owner.Abort();

        owner.IsCompleted.ShouldBeTrue();
        owner.IsAborted.ShouldBeFalse();
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

    private static void AssertHandlerRegistrationIsRejected(IUnitOfWork unitOfWork)
    {
        Should.Throw<InvalidOperationException>(() =>
            unitOfWork.OnCompleted(_ => Task.CompletedTask));
        Should.Throw<InvalidOperationException>(() =>
            unitOfWork.OnFailed((_, _) => Task.CompletedTask));
        Should.Throw<InvalidOperationException>(() =>
            unitOfWork.OnDisposed(_ => { }));
    }
}
