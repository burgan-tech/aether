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
    public async Task EnqueueAsync_WithAmbientUoW_PersistsPendingRowAndDoesNotCallScheduler()
    {
        // Arrange — an ambient UoW is active.
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        // Act
        var jobId = await _sut.EnqueueAsync(
            "handler", "job-1", new { X = 1 }, "@every 5s",
            metadata: null, failurePolicyOptions: null, useAmbientUnitOfWork: true,
            cancellationToken: CancellationToken.None);

        // Assert — a Pending row is saved into the ambient UoW; NO own UoW opened, NO self-commit, and
        // the scheduler is NEVER called (the arming poller arms it after the caller's commit).
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
        // Arrange — ambient UoW so we hit the transactional path; capture the saved job entity.
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        var suppliedId = Guid.NewGuid();

        // Act
        var returnedId = await _sut.EnqueueAsync(
            "handler", "job-3", new { X = 1 }, "@every 5s",
            metadata: null, failurePolicyOptions: null, useAmbientUnitOfWork: true,
            jobId: suppliedId, cancellationToken: CancellationToken.None);

        // Assert — the caller-supplied id is used for the entity and returned.
        returnedId.ShouldBe(suppliedId);
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(suppliedId);
    }

    [Fact]
    public async Task EnqueueAsync_OptInButNoAmbientUoW_FallsBackToOwnRequiresNewUoW()
    {
        // Arrange — opt-in requested but NO ambient UoW → legacy self-contained path.
        _uowManager.Current.Returns((IUnitOfWork?)null);
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);

        // Act
        await _sut.EnqueueAsync(
            "handler", "job-2", new { X = 1 }, "@every 5s",
            metadata: null, failurePolicyOptions: null, useAmbientUnitOfWork: true,
            cancellationToken: CancellationToken.None);

        // Assert — opened and committed its own RequiresNew UoW; saved a row; never called the scheduler.
        _uowManager.Received(1).Begin(Arg.Any<UnitOfWorkOptions>());
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        await ownUow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _jobScheduler.DidNotReceiveWithAnyArgs().ScheduleAsync(default!, default!, default!, default);
    }
}
