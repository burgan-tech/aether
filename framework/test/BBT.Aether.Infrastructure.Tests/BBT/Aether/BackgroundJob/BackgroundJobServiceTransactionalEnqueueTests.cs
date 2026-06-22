using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.BackgroundJob;

/// <summary>
/// Tests the enqueue paths of <see cref="BackgroundJobService"/> after the arming-poller refactor:
/// enqueue writes ONLY a <see cref="BackgroundJobStatus.Pending"/> row (atomically, inside the caller's
/// UoW on the ambient path) and NEVER calls the scheduler — the arming poller arms the row after commit.
/// </summary>
public class BackgroundJobServiceTransactionalEnqueueTests
{
    private readonly IJobStore _jobStore = Substitute.For<IJobStore>();
    private readonly IJobScheduler _jobScheduler = Substitute.For<IJobScheduler>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentSchema _currentSchema = Substitute.For<ICurrentSchema>();
    private readonly IEventSerializer _eventSerializer = Substitute.For<IEventSerializer>();
    private readonly BackgroundJobOptions _options = new() { MaxRetryCount = 7 };
    private readonly BackgroundJobService _sut;

    public BackgroundJobServiceTransactionalEnqueueTests()
    {
        _guidGenerator.Create().Returns(Guid.NewGuid());
        _clock.UtcNow.Returns(DateTime.UtcNow);
        _currentSchema.Name.Returns("runtime_test");
        _eventSerializer.Serialize(Arg.Any<object>()).Returns(new byte[] { 1, 2, 3 });
        _sut = new BackgroundJobService(
            _jobStore, _jobScheduler, _uowManager, _guidGenerator, _clock,
            _currentSchema, _eventSerializer, _options,
            Substitute.For<ILogger<BackgroundJobService>>());
    }

    [Fact]
    public async Task EnqueueAsync_DefaultAmbient_PersistsPendingRowAndDoesNotCallScheduler()
    {
        // Arrange — an ambient UoW is active; default mode is Ambient, directly:false.
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        // Act
        var jobId = await _sut.EnqueueAsync(
            "handler", "job-1", new { X = 1 }, "@every 5s",
            cancellationToken: CancellationToken.None);

        // Assert — a Pending row is saved into the ambient UoW; NO own UoW opened, NO self-commit, and
        // the scheduler is NEVER called (directly:false → the arming poller arms it after the commit).
        jobId.ShouldNotBe(Guid.Empty);
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        saved.ShouldNotBeNull();
        saved!.Status.ShouldBe(BackgroundJobStatus.Pending);
        saved.Kind.ShouldBe(JobKind.Recurring);          // "@every 5s" → recurring
        saved.MaxRetryCount.ShouldBe(_options.MaxRetryCount);
        saved.NextRetryAt.ShouldNotBeNull();
        _uowManager.DidNotReceive().Begin(Arg.Any<UnitOfWorkOptions>());
        await ambientUow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _jobScheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default!, default!, default!, default);
        ambientUow.DidNotReceiveWithAnyArgs().OnCompleted(default!);
    }

    [Fact]
    public async Task EnqueueAsync_WithSuppliedJobId_PersistsAndReturnsThatId()
    {
        // Arrange — ambient UoW so we hit the ambient path; capture the saved job entity.
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        var suppliedId = Guid.NewGuid();

        // Act
        var returnedId = await _sut.EnqueueAsync(
            "handler", "job-3", new { X = 1 }, "@every 5s",
            jobId: suppliedId, cancellationToken: CancellationToken.None);

        // Assert — the caller-supplied id is used for the entity and returned.
        returnedId.ShouldBe(suppliedId);
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(suppliedId);
    }

    [Fact]
    public async Task EnqueueAsync_NoAmbientUoW_OpensOwnRequiresNewUoW()
    {
        // Arrange — NO ambient UoW → standalone RequiresNew path even in default Ambient mode.
        _uowManager.Current.Returns((IUnitOfWork?)null);
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);

        // Act
        await _sut.EnqueueAsync(
            "handler", "job-2", new { X = 1 }, "@every 5s",
            cancellationToken: CancellationToken.None);

        // Assert — opened and committed its own RequiresNew UoW; saved a row; never called the scheduler.
        _uowManager.Received(1).Begin(Arg.Any<UnitOfWorkOptions>());
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        await ownUow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _jobScheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task EnqueueAsync_Standalone_AlwaysOpensOwnUoW_IgnoringAmbient()
    {
        // Arrange — an ambient UoW IS active but Standalone must ignore it and open its own RequiresNew UoW.
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);

        // Act
        await _sut.EnqueueAsync(
            "handler", "job-standalone", new { X = 1 }, "@every 5s",
            mode: JobEnqueueMode.Standalone, cancellationToken: CancellationToken.None);

        // Assert — own UoW opened and committed; ambient UoW untouched; no scheduler call (directly:false).
        _uowManager.Received(1).Begin(Arg.Any<UnitOfWorkOptions>());
        await ownUow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        ambientUow.DidNotReceiveWithAnyArgs().OnCompleted(default!);
        await _jobScheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task EnqueueAsync_Standalone_Directly_ArmsImmediatelyAfterCommit()
    {
        // Arrange — no ambient; Standalone + directly arms the scheduler inline after the commit and CASes
        // the row Pending → Scheduled in its own UoW.
        _uowManager.Current.Returns((IUnitOfWork?)null);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ => Substitute.For<IUnitOfWork>());

        // Act
        var id = await _sut.EnqueueAsync(
            "handler", "job-direct", new { X = 1 }, "@every 5s",
            mode: JobEnqueueMode.Standalone, directly: true, cancellationToken: CancellationToken.None);

        // Assert — scheduler armed once, and the CAS transition Pending → Scheduled was attempted.
        await _jobScheduler.Received(1).ScheduleAsync(
            "handler", "job-direct", "@every 5s", Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<JobScheduleFailurePolicy?>(), Arg.Any<CancellationToken>());
        await _jobStore.Received(1).TryTransitionStatusAsync(
            id, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_Ambient_Directly_DefersArmToOnCompleted()
    {
        // Arrange — ambient UoW active; directly:true must register an OnCompleted callback and NOT arm
        // the scheduler synchronously. Capture the callback and invoke it to simulate commit.
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        // The ArmNowAsync CAS opens its own UoW.
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ => Substitute.For<IUnitOfWork>());

        Func<IUnitOfWork, Task>? onCompleted = null;
        ambientUow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(cb => onCompleted = cb))
            .Returns(Substitute.For<IDisposable>());

        // Act
        var id = await _sut.EnqueueAsync(
            "handler", "job-amb-direct", new { X = 1 }, "@every 5s",
            directly: true, cancellationToken: CancellationToken.None);

        // Assert — saved into ambient; an OnCompleted callback was registered; nothing armed yet.
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        ambientUow.Received(1).OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());
        await _jobScheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default!, default!, default!, default);
        onCompleted.ShouldNotBeNull();

        // Simulate the caller's commit → the callback arms the scheduler and CASes the row.
        await onCompleted!(ambientUow);

        await _jobScheduler.Received(1).ScheduleAsync(
            "handler", "job-amb-direct", "@every 5s", Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<JobScheduleFailurePolicy?>(), Arg.Any<CancellationToken>());
        await _jobStore.Received(1).TryTransitionStatusAsync(
            id, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled, Arg.Any<CancellationToken>());
    }
}
