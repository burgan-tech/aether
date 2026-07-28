using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events.Processing;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxSignalCoordinatorTests
{
    [Fact]
    public async Task WaitAsync_returns_immediately_when_a_signal_is_already_pending()
    {
        var coordinator = new OutboxSignalCoordinator();
        coordinator.Signal("sys_queues", 3);

        var sw = Stopwatch.StartNew();
        var keys = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        sw.Stop();

        keys.Count.ShouldBe(1);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_returns_empty_after_the_fallback_timeout()
    {
        var coordinator = new OutboxSignalCoordinator();

        var keys = await coordinator.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);

        keys.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signal_arriving_while_waiting_wakes_the_waiter()
    {
        var coordinator = new OutboxSignalCoordinator();

        var waiting = coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);
        coordinator.Signal("sys_queues", 9);

        var keys = await waiting;

        keys.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ten_thousand_signals_for_one_partition_collapse_to_one_key()
    {
        var coordinator = new OutboxSignalCoordinator();
        for (var i = 0; i < 10_000; i++) coordinator.Signal("sys_queues", 2);

        var keys = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        keys.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Distinct_partitions_are_kept_apart()
    {
        var coordinator = new OutboxSignalCoordinator();
        coordinator.Signal("sys_queues", 1);
        coordinator.Signal("sys_queues", 2);
        coordinator.Signal("other_schema", 1);

        var keys = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        keys.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Pending_keys_are_drained_and_not_returned_twice()
    {
        var coordinator = new OutboxSignalCoordinator();
        coordinator.Signal("sys_queues", 1);

        var first = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var second = await coordinator.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);

        first.Count.ShouldBe(1);
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task WaitAsync_honours_the_caller_cancellation_token()
    {
        var coordinator = new OutboxSignalCoordinator();
        using var cts = new CancellationTokenSource();
        var waiting = coordinator.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        await Task.Delay(50);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public void Signal_never_blocks_even_with_no_waiter()
    {
        var coordinator = new OutboxSignalCoordinator();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++) coordinator.Signal("sys_queues", (short)(i % 64));
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}
