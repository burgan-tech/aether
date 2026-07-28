using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events;

public sealed class OutboxSignalCollectorTests
{
    private sealed class RecordingPublisher : IOutboxWakeupPublisher
    {
        public List<OutboxWakeupSignal> Published { get; } = [];
        public bool ThrowOnPublish { get; set; }

        public Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("broker down");
            Published.Add(signal);
            return Task.FromResult(true);
        }
    }

    /// <summary>Captures the OnCompleted handler so a test can fire it like a real commit.</summary>
    private static (IUnitOfWorkManager Manager, Func<Task> FireCommit) FakeUow()
    {
        Func<IUnitOfWork, Task>? handler = null;
        var uow = Substitute.For<IUnitOfWork>();
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(h => handler = h))
           .Returns(Substitute.For<IDisposable>());

        var manager = Substitute.For<IUnitOfWorkManager>();
        manager.Current.Returns(uow);

        return (manager, () => handler is null ? Task.CompletedTask : handler(uow));
    }

    private static AetherOutboxOptions Options(bool enabled = true) =>
        new() { Schema = "sys_queues", SignalEnabled = enabled };

    private static OutboxSignalCollector NewCollector(
        IUnitOfWorkManager manager, IOutboxWakeupPublisher publisher, AetherOutboxOptions options) =>
        new(manager, publisher, options, NullLogger<OutboxSignalCollector>.Instance);

    [Fact]
    public async Task Many_rows_in_one_transaction_produce_one_signal_per_partition()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = NewCollector(manager, publisher, Options());

        for (var i = 0; i < 100; i++) collector.Mark("sys_queues", 7);

        publisher.Published.ShouldBeEmpty();   // nothing before commit
        await fireCommit();

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].Schema.ShouldBe("sys_queues");
        publisher.Published[0].PartitionId.ShouldBe((short)7);
    }

    [Fact]
    public async Task Distinct_partitions_each_get_their_own_signal()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = NewCollector(manager, publisher, Options());

        collector.Mark("sys_queues", 1);
        collector.Mark("sys_queues", 2);
        collector.Mark("sys_queues", 1);

        await fireCommit();

        publisher.Published.Count.ShouldBe(2);
    }

    [Fact]
    public void Nothing_is_published_when_the_transaction_never_commits()
    {
        var publisher = new RecordingPublisher();
        var (manager, _) = FakeUow();
        var collector = NewCollector(manager, publisher, Options());

        collector.Mark("sys_queues", 3);
        // commit handler deliberately never fired — simulates rollback

        publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_publisher_does_not_escape_the_commit_handler()
    {
        // The business transaction has already committed. A broker problem must never surface
        // here, or a successful commit would look like a failed request.
        var publisher = new RecordingPublisher { ThrowOnPublish = true };
        var (manager, fireCommit) = FakeUow();
        var collector = NewCollector(manager, publisher, Options());

        collector.Mark("sys_queues", 4);

        await Should.NotThrowAsync(fireCommit);
    }

    [Fact]
    public async Task Marking_is_inert_when_signalling_is_disabled()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = NewCollector(manager, publisher, Options(enabled: false));

        collector.Mark("sys_queues", 5);
        await fireCommit();

        publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Too_many_partitions_collapse_into_a_single_check_all_signal()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = NewCollector(manager, publisher, Options());

        for (short p = 0; p < 40; p++) collector.Mark("sys_queues", p);

        await fireCommit();

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].PartitionId.ShouldBe(OutboxWakeupSignal.AllPartitions);
    }

    [Fact]
    public async Task The_commit_hook_is_registered_only_once_however_many_marks()
    {
        var publisher = new RecordingPublisher();
        Func<IUnitOfWork, Task>? handler = null;
        var uow = Substitute.For<IUnitOfWork>();
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(h => handler = h))
           .Returns(Substitute.For<IDisposable>());
        var manager = Substitute.For<IUnitOfWorkManager>();
        manager.Current.Returns(uow);

        var collector = NewCollector(manager, publisher, Options());
        for (var i = 0; i < 50; i++) collector.Mark("sys_queues", (short)(i % 3));

        uow.Received(1).OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());

        await handler!(uow);
        publisher.Published.Count.ShouldBe(3);
    }

    [Fact]
    public void Marking_without_an_ambient_transaction_does_not_throw()
    {
        var publisher = new RecordingPublisher();
        var manager = Substitute.For<IUnitOfWorkManager>();
        manager.Current.Returns((IUnitOfWork?)null);

        var collector = NewCollector(manager, publisher, Options());

        Should.NotThrow(() => collector.Mark("sys_queues", 6));
        publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_collapse_threshold_scales_with_the_configured_partition_count()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var options = new AetherOutboxOptions
        {
            Schema = "sys_queues", SignalEnabled = true, PartitionCount = 8
        };
        var collector = NewCollector(manager, publisher, options);

        // 8 partitions with a fixed threshold of 16 would never collapse; derived it must.
        for (short p = 0; p < 8; p++) collector.Mark("sys_queues", p);

        await fireCommit();

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].PartitionId.ShouldBe(OutboxWakeupSignal.AllPartitions);
    }
}
