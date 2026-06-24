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
using NSubstitute.ExceptionExtensions;
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
    public async Task EnqueueAsync_Directly_NoAmbient_SavesAsScheduledAndArmsSchedulerWithoutCasTransition()
    {
        // Arrange
        _uowManager.Current.Returns((IUnitOfWork?)null);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ => Substitute.For<IUnitOfWork>());
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        // Act
        var id = await _sut.EnqueueAsync(
            "handler", "job-direct", new { X = 1 }, "@every 5s",
            directly: true, cancellationToken: CancellationToken.None);

        // Assert — row persisted as Scheduled (not Pending) so arming poller cannot race.
        saved.ShouldNotBeNull();
        saved!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        await _jobScheduler.Received(1).ScheduleAsync(
            "handler", "job-direct", "@every 5s", Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<JobScheduleFailurePolicy?>(), Arg.Any<CancellationToken>());
        // No CAS Pending→Scheduled — row was already Scheduled at save time
        await _jobStore.DidNotReceive().TryTransitionStatusAsync(
            id, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_Directly_SchedulerFails_RollsBackToScheduledPending()
    {
        // Arrange — scheduler throws; ArmNowAsync should CAS Scheduled→Pending for the arming poller.
        _uowManager.Current.Returns((IUnitOfWork?)null);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ => Substitute.For<IUnitOfWork>());
        _jobScheduler.ScheduleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<JobScheduleFailurePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("scheduler offline"));

        // Act — must NOT throw (ArmNowAsync swallows the error and logs)
        var id = await _sut.EnqueueAsync(
            "handler", "job-direct-fail", new { X = 1 }, "@every 5s",
            directly: true, cancellationToken: CancellationToken.None);

        // Assert — rollback CAS Scheduled→Pending attempted so arming poller picks it up
        await _jobStore.Received(1).TryTransitionStatusAsync(
            id, BackgroundJobStatus.Scheduled, BackgroundJobStatus.Pending, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_Ambient_Directly_SavesAsScheduledAndDefersArmToOnCompleted()
    {
        // Arrange — ambient UoW active; directly:true must register an OnCompleted callback and NOT arm
        // the scheduler synchronously. Saved row must be Scheduled (not Pending).
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ => Substitute.For<IUnitOfWork>());
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        Func<IUnitOfWork, Task>? onCompleted = null;
        ambientUow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(cb => onCompleted = cb))
            .Returns(Substitute.For<IDisposable>());

        // Act
        var id = await _sut.EnqueueAsync(
            "handler", "job-amb-direct", new { X = 1 }, "@every 5s",
            directly: true, cancellationToken: CancellationToken.None);

        // Assert — saved as Scheduled; callback registered; scheduler not yet called
        saved.ShouldNotBeNull();
        saved!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        ambientUow.Received(1).OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());
        await _jobScheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default!, default!, default!, default);
        onCompleted.ShouldNotBeNull();

        // Simulate the caller's commit → callback fires and arms the scheduler (no CAS transition)
        await onCompleted!(ambientUow);

        await _jobScheduler.Received(1).ScheduleAsync(
            "handler", "job-amb-direct", "@every 5s", Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<JobScheduleFailurePolicy?>(), Arg.Any<CancellationToken>());
        await _jobStore.DidNotReceive().TryTransitionStatusAsync(
            id, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled, Arg.Any<CancellationToken>());
    }
}
