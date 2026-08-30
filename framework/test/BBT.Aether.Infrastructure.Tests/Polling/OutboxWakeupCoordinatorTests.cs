using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.Polling;

public sealed class OutboxWakeupCoordinatorTests
{
    private static async Task<bool> PollUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task OnOutboxMessageStored_WakeupSignalDisabled_DoesNotRegisterOrNotify()
    {
        var options = new AetherOutboxOptions { WakeupSignalEnabled = false };
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        var uow = Substitute.For<IUnitOfWork>();
        unitOfWorkManager.Current.Returns(uow);
        var notifier = Substitute.For<IOutboxWakeupNotifier>();

        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);

        sut.OnOutboxMessageStored();

        uow.DidNotReceive().OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());
        await Task.Delay(50);
        await notifier.DidNotReceive().NotifyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnOutboxMessageStored_CalledTwiceUnderSameUow_RegistersOnCompletedExactlyOnce()
    {
        var options = new AetherOutboxOptions { WakeupSignalEnabled = true };
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        var uow = Substitute.For<IUnitOfWork>();
        unitOfWorkManager.Current.Returns(uow);
        var notifier = Substitute.For<IOutboxWakeupNotifier>();

        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);

        sut.OnOutboxMessageStored();
        sut.OnOutboxMessageStored();

        uow.Received(1).OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());
    }

    [Fact]
    public async Task OnCompletedCallback_Invoked_CallsNotifierWithoutAwaitingItInline()
    {
        var options = new AetherOutboxOptions { WakeupSignalEnabled = true };
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        var uow = Substitute.For<IUnitOfWork>();
        unitOfWorkManager.Current.Returns(uow);
        var notifier = Substitute.For<IOutboxWakeupNotifier>();
        notifier.NotifyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Func<IUnitOfWork, Task>? capturedCallback = null;
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(cb => capturedCallback = cb));

        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);
        sut.OnOutboxMessageStored();

        capturedCallback.ShouldNotBeNull();

        // The callback itself must return immediately (detached publish), not await the notify.
        var callbackTask = capturedCallback!(uow);
        callbackTask.IsCompleted.ShouldBeTrue();

        var notified = await PollUntilAsync(
            () => notifier.ReceivedCalls().Count() > 0,
            TimeSpan.FromSeconds(1));
        notified.ShouldBeTrue();

        await notifier.Received(1).NotifyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnCompletedCallback_NotifierThrows_CallbackStillCompletesAndDoesNotPropagate()
    {
        var options = new AetherOutboxOptions { WakeupSignalEnabled = true };
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        var uow = Substitute.For<IUnitOfWork>();
        unitOfWorkManager.Current.Returns(uow);
        var notifier = Substitute.For<IOutboxWakeupNotifier>();
        notifier.NotifyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("broker unavailable")));

        Func<IUnitOfWork, Task>? capturedCallback = null;
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(cb => capturedCallback = cb));

        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);
        sut.OnOutboxMessageStored();

        capturedCallback.ShouldNotBeNull();

        // Should not throw even though the underlying notify task faults.
        await capturedCallback!(uow);

        var notified = await PollUntilAsync(
            () => notifier.ReceivedCalls().Count() > 0,
            TimeSpan.FromSeconds(1));
        notified.ShouldBeTrue();
    }

    [Fact]
    public async Task OnOutboxMessageStored_NoAmbientUow_NotifiesWithoutRegistration()
    {
        var options = new AetherOutboxOptions { WakeupSignalEnabled = true };
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        unitOfWorkManager.Current.Returns((IUnitOfWork?)null);
        var notifier = Substitute.For<IOutboxWakeupNotifier>();
        notifier.NotifyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);
        sut.OnOutboxMessageStored();

        var notified = await PollUntilAsync(
            () => notifier.ReceivedCalls().Count() > 0,
            TimeSpan.FromSeconds(1));
        notified.ShouldBeTrue();

        await notifier.Received(1).NotifyAsync(Arg.Any<CancellationToken>());
    }

    // Item 6 (documented, not tested): rollback needs no test because the coordinator only ever
    // registers via IUnitOfWork.OnCompleted, which Aether's UoW implementation fires solely on
    // successful commit — a rollback path never invokes the registered callback, so there is no
    // coordinator-owned behavior to assert here.

    [Fact]
    public async Task OnOutboxMessageStored_TwoNestedScopesSharingOneRoot_FiresExactlyOneNotify()
    {
        // Reproduces the amplification bug: a `Required` participant scope is a distinct
        // UnitOfWorkScope object per nesting level, but both forward OnCompleted to the SAME
        // CompositeUnitOfWork root. Two EfCoreOutboxStore.StoreAsync calls at different nesting
        // depths within one logical commit must still yield exactly one nudge, not one per scope.
        var options = new AetherOutboxOptions { WakeupSignalEnabled = true };
        var notifier = Substitute.For<IOutboxWakeupNotifier>();
        notifier.NotifyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var root = new CompositeUnitOfWork(serviceProvider);
        root.InitializeCore(new UnitOfWorkOptions());

        var ambient = new FakeAmbientAccessor();
        var outerScope = new UnitOfWorkScope(root, ambient, ownsRoot: true);
        var innerScope = new UnitOfWorkScope(root, ambient, ownsRoot: false);

        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);

        // Outer (owning) scope stores a message first...
        unitOfWorkManager.Current.Returns(outerScope);
        sut.OnOutboxMessageStored();

        // ...then a nested Required participant, sharing the same root, stores another.
        unitOfWorkManager.Current.Returns(innerScope);
        sut.OnOutboxMessageStored();

        await root.CommitAsync();

        var notified = await PollUntilAsync(
            () => notifier.ReceivedCalls().Count() > 0,
            TimeSpan.FromSeconds(1));
        notified.ShouldBeTrue();

        await notifier.Received(1).NotifyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnCompletedCallback_Invoked_WithAmbientActivity_NotifierSeesNoAmbientActivity()
    {
        // Reproduces the trace-leak bug: the OnCompleted callback runs inside the committing
        // business transaction's ExecutionContext, which still has an ambient Activity flowing
        // through Task.Run. The notify must be severed from it — the nudge is infrastructure,
        // not business flow, and must never attach to (or propagate the traceparent of) the
        // business trace that triggered it.
        var options = new AetherOutboxOptions { WakeupSignalEnabled = true };
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        var uow = Substitute.For<IUnitOfWork>();
        unitOfWorkManager.Current.Returns(uow);

        Activity? activitySeenByNotifier = null;
        var notifierInvoked = false;
        var notifier = Substitute.For<IOutboxWakeupNotifier>();
        notifier.NotifyAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            activitySeenByNotifier = Activity.Current;
            notifierInvoked = true;
            return Task.CompletedTask;
        });

        Func<IUnitOfWork, Task>? capturedCallback = null;
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(cb => capturedCallback = cb));

        var sut = new OutboxWakeupCoordinator(options, unitOfWorkManager, notifier);
        sut.OnOutboxMessageStored();
        capturedCallback.ShouldNotBeNull();

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activitySource = new ActivitySource(nameof(OutboxWakeupCoordinatorTests) + ".AmbientActivity");
        using var ambientActivity = activitySource.StartActivity("business-transition");
        ambientActivity.ShouldNotBeNull();
        Activity.Current.ShouldBe(ambientActivity);

        // Fire the OnCompleted callback while the ambient business Activity is current, exactly as
        // it happens inside CommitAsync in production.
        var callbackTask = capturedCallback!(uow);
        callbackTask.IsCompleted.ShouldBeTrue();

        var notified = await PollUntilAsync(() => notifierInvoked, TimeSpan.FromSeconds(1));
        notified.ShouldBeTrue();

        activitySeenByNotifier.ShouldBeNull();
    }

    private sealed class FakeAmbientAccessor : IAmbientUnitOfWorkAccessor
    {
        public IUnitOfWork? Current { get; set; }

        public IUnitOfWork? GetActiveUnitOfWork() => Current;
    }
}
