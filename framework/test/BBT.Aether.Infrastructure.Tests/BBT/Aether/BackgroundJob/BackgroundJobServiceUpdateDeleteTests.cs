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

public class BackgroundJobServiceUpdateDeleteTests
{
    private readonly IJobStore _jobStore = Substitute.For<IJobStore>();
    private readonly IJobScheduler _jobScheduler = Substitute.For<IJobScheduler>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentSchema _currentSchema = Substitute.For<ICurrentSchema>();
    private readonly IEventSerializer _eventSerializer = Substitute.For<IEventSerializer>();
    private readonly BackgroundJobOptions _options = new() { MaxRetryCount = 3 };
    private readonly BackgroundJobService _sut;

    public BackgroundJobServiceUpdateDeleteTests()
    {
        _guidGenerator.Create().Returns(Guid.NewGuid());
        _clock.UtcNow.Returns(DateTime.UtcNow);
        _currentSchema.Name.Returns("runtime_test");
        _sut = new BackgroundJobService(
            _jobStore, _jobScheduler, _uowManager, _guidGenerator, _clock,
            _currentSchema, _eventSerializer, _options,
            Substitute.For<ILogger<BackgroundJobService>>());
    }

    // ─── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_Ambient_UsesAmbientUoW_DoesNotOpenOwnUoW()
    {
        // Arrange
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        var jobId = Guid.NewGuid();
        var existing = new BackgroundJobInfo(jobId, "handler", "job-1")
            { ExpressionValue = "@every 5s", Status = BackgroundJobStatus.Scheduled };
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>()).Returns(existing);
        BackgroundJobInfo? saved = null;
        await _jobStore.SaveAsync(Arg.Do<BackgroundJobInfo>(j => saved = j), Arg.Any<CancellationToken>());

        // Act
        await _sut.UpdateAsync(jobId, "@every 10s");

        // Assert — updates happen in ambient; no own UoW opened, no commit called
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        saved.ShouldNotBeNull();
        saved!.ExpressionValue.ShouldBe("@every 10s");
        saved!.Status.ShouldBe(BackgroundJobStatus.Pending);
        saved!.Kind.ShouldBe(JobKind.Recurring);
        _uowManager.DidNotReceive().Begin(Arg.Any<UnitOfWorkOptions>());
        await ambientUow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_NoAmbient_OpensOwnRequiresNewUoW()
    {
        // Arrange
        _uowManager.Current.Returns((IUnitOfWork?)null);
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);
        var jobId = Guid.NewGuid();
        var existing = new BackgroundJobInfo(jobId, "handler", "job-1")
            { ExpressionValue = "@every 5s", Status = BackgroundJobStatus.Scheduled };
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>()).Returns(existing);

        // Act
        await _sut.UpdateAsync(jobId, "@every 10s");

        // Assert — opens own UoW, saves, commits
        _uowManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew && o.IsTransactional));
        await _jobStore.Received(1).SaveAsync(Arg.Any<BackgroundJobInfo>(), Arg.Any<CancellationToken>());
        await ownUow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Ambient_JobNotFound_Throws()
    {
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        var jobId = Guid.NewGuid();
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>()).Returns((BackgroundJobInfo?)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateAsync(jobId, "@every 10s"));
    }

    // ─── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_Ambient_UsesAmbientUoW_DoesNotOpenOwnUoW()
    {
        // Arrange
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        var jobId = Guid.NewGuid();
        var existing = new BackgroundJobInfo(jobId, "handler", "job-1")
            { Status = BackgroundJobStatus.Scheduled };
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>()).Returns(existing);

        // Act
        var result = await _sut.DeleteAsync(jobId);

        // Assert — deletes from scheduler, updates DB status; no own UoW, no commit
        result.ShouldBeTrue();
        await _jobScheduler.Received(1).DeleteAsync("handler", "job-1", Arg.Any<CancellationToken>());
        await _jobStore.Received(1).UpdateStatusAsync(
            jobId, BackgroundJobStatus.Cancelled, Arg.Any<DateTime>(),
            cancellationToken: Arg.Any<CancellationToken>());
        _uowManager.DidNotReceive().Begin(Arg.Any<UnitOfWorkOptions>());
        await ambientUow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NoAmbient_OpensOwnRequiresNewUoW()
    {
        // Arrange
        _uowManager.Current.Returns((IUnitOfWork?)null);
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);
        var jobId = Guid.NewGuid();
        var existing = new BackgroundJobInfo(jobId, "handler", "job-1")
            { Status = BackgroundJobStatus.Scheduled };
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>()).Returns(existing);

        // Act
        var result = await _sut.DeleteAsync(jobId);

        // Assert — opens own RequiresNew transactional UoW, commits after scheduler + store calls
        result.ShouldBeTrue();
        _uowManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew && o.IsTransactional));
        await _jobScheduler.Received(1).DeleteAsync("handler", "job-1", Arg.Any<CancellationToken>());
        await _jobStore.Received(1).UpdateStatusAsync(
            jobId, BackgroundJobStatus.Cancelled, Arg.Any<DateTime>(),
            cancellationToken: Arg.Any<CancellationToken>());
        await ownUow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Ambient_JobNotFound_ReturnsFalse()
    {
        var ambientUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambientUow);
        var jobId = Guid.NewGuid();
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>()).Returns((BackgroundJobInfo?)null);

        var result = await _sut.DeleteAsync(jobId);

        result.ShouldBeFalse();
        await _jobScheduler.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default);
    }
}
