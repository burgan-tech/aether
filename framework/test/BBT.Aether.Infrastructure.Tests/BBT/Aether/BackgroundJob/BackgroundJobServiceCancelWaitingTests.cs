using System;
using System.Linq;
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

public sealed class BackgroundJobServiceCancelWaitingTests
{
    private readonly IJobStore _jobStore = Substitute.For<IJobStore>();
    private readonly IJobScheduler _jobScheduler = Substitute.For<IJobScheduler>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<BackgroundJobService> _logger = Substitute.For<ILogger<BackgroundJobService>>();
    private readonly BackgroundJobService _sut;

    public BackgroundJobServiceCancelWaitingTests()
    {
        _clock.UtcNow.Returns(DateTime.UtcNow);
        _sut = new BackgroundJobService(
            _jobStore,
            _jobScheduler,
            _uowManager,
            Substitute.For<IGuidGenerator>(),
            _clock,
            Substitute.For<ICurrentSchema>(),
            Substitute.For<IEventSerializer>(),
            new BackgroundJobOptions(),
            _logger);
    }

    [Fact]
    public async Task Running_returns_skipped_and_never_deletes_scheduler()
    {
        var id = Guid.NewGuid();
        var running = NewJob(id, BackgroundJobStatus.Running);
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(running, running);
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CancelWaitingAsync(id);

        result.ShouldBe(BackgroundJobCancellationResult.SkippedRunning);
        await _jobScheduler.DidNotReceiveWithAnyArgs()
            .DeleteAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(BackgroundJobStatus.Completed)]
    [InlineData(BackgroundJobStatus.Failed)]
    [InlineData(BackgroundJobStatus.Cancelled)]
    public async Task Terminal_returns_already_terminal(BackgroundJobStatus status)
    {
        var id = Guid.NewGuid();
        var terminal = NewJob(id, status);
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(terminal, terminal);
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.AlreadyTerminal);
    }

    [Fact]
    public async Task Missing_returns_not_found_without_store_transition()
    {
        var id = Guid.NewGuid();
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns((BackgroundJobInfo?)null);

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.NotFound);
        await _jobStore.DidNotReceiveWithAnyArgs()
            .TryCancelWaitingAsync(default, default, default);
    }

    [Fact]
    public async Task Waiting_classification_retries_atomic_cancel_once()
    {
        var id = Guid.NewGuid();
        var pending = NewJob(id, BackgroundJobStatus.Pending);
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(pending, pending);
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false, true);
        var ambient = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambient);
        ambient.OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>())
            .Returns(Substitute.For<IDisposable>());

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.Cancelled);

        await _jobStore.Received(2).TryCancelWaitingAsync(
            id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Waiting_classification_throws_after_one_retry()
    {
        var id = Guid.NewGuid();
        var pending = NewJob(id, BackgroundJobStatus.Pending);
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(pending);
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _uowManager.Current.Returns(Substitute.For<IUnitOfWork>());

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CancelWaitingAsync(id));

        exception.Message.ShouldContain("after one retry");
        await _jobStore.Received(2).TryCancelWaitingAsync(
            id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _jobScheduler.DidNotReceiveWithAnyArgs()
            .DeleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task Ambient_cancellation_defers_scheduler_delete()
    {
        var ambient = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambient);
        var id = Guid.NewGuid();
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(NewJob(id, BackgroundJobStatus.Scheduled));
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        Func<IUnitOfWork, Task>? callback = null;
        ambient.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(value => callback = value))
            .Returns(Substitute.For<IDisposable>());

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.Cancelled);
        await _jobScheduler.DidNotReceiveWithAnyArgs()
            .DeleteAsync(default!, default!, default);

        await callback!(ambient);
        await _jobScheduler.Received(1)
            .DeleteAsync("handler", "job-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ambient_cancellation_without_completion_never_deletes_scheduler()
    {
        var ambient = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns(ambient);
        var id = Guid.NewGuid();
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(NewJob(id, BackgroundJobStatus.Pending));
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        ambient.OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>())
            .Returns(Substitute.For<IDisposable>());

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.Cancelled);

        ambient.Received(1).OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>());
        await _jobScheduler.DidNotReceiveWithAnyArgs()
            .DeleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task Non_ambient_cancellation_commits_before_scheduler_delete()
    {
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns((IUnitOfWork?)null);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);
        var id = Guid.NewGuid();
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(NewJob(id, BackgroundJobStatus.Pending));
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.Cancelled);

        Received.InOrder(() =>
        {
            _jobStore.TryCancelWaitingAsync(
                id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            ownUow.CommitAsync(Arg.Any<CancellationToken>());
            _jobScheduler.DeleteAsync(
                "handler", "job-1", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Scheduler_delete_failure_does_not_undo_cancelled_result()
    {
        var ownUow = Substitute.For<IUnitOfWork>();
        _uowManager.Current.Returns((IUnitOfWork?)null);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);
        var id = Guid.NewGuid();
        _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(NewJob(id, BackgroundJobStatus.Scheduled));
        _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _jobScheduler.DeleteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("scheduler offline"));

        (await _sut.CancelWaitingAsync(id))
            .ShouldBe(BackgroundJobCancellationResult.Cancelled);

        await ownUow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        HasLog(
                LogLevel.Error,
                "Background job 'job-1' was cancelled in persistence but could not be deleted from the scheduler")
            .ShouldBeTrue();
    }

    private bool HasLog(LogLevel level, string message)
    {
        return _logger.ReceivedCalls().Any(call =>
        {
            var arguments = call.GetArguments();
            return arguments.Length >= 3
                   && arguments[0] is LogLevel actualLevel
                   && actualLevel == level
                   && string.Equals(arguments[2]?.ToString(), message, StringComparison.Ordinal);
        });
    }

    private static BackgroundJobInfo NewJob(Guid id, BackgroundJobStatus status) =>
        new(id, "handler", "job-1") { Status = status };
}
