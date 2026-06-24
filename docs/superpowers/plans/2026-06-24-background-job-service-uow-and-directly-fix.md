# BackgroundJobService UoW Awareness & Directly Double-Schedule Fix

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `UpdateAsync` and `DeleteAsync` participate in an ambient Unit of Work when one exists (matching the `EnqueueAsync` pattern), and fix the double-schedule race in the `directly: true` path of `EnqueueAsync`.

**Architecture:** `BackgroundJobService` already handles the ambient/standalone split in `EnqueueAsync`. The two other mutating methods (`UpdateAsync`, `DeleteAsync`) always open their own UoW — this prevents callers from batching a job update/delete with other business changes in one atomic transaction. The `directly` bug is separate: saving a row as `Pending` then later CAS-ing to `Scheduled` leaves a window where the arming poller can also call the scheduler, resulting in two scheduler registrations. The fix is to save directly as `Scheduled` when `directly: true` and roll back to `Pending` only if the immediate scheduler call fails.

**Tech Stack:** .NET 10, xUnit, NSubstitute, Shouldly, `IUnitOfWorkManager`, `IJobStore`, `IJobScheduler`

---

## File Map

| File | Change |
|---|---|
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs` | Tasks 1 + 2 |
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceTransactionalEnqueueTests.cs` | Task 1 — update `directly` tests |
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceUpdateDeleteTests.cs` _(new)_ | Task 2 — new test file |

---

## Task 1: Fix `directly: true` double-schedule race

**Problem:** `EnqueueAsync` saves `Status = Pending`, then `ArmNowAsync` calls the scheduler and CAS-es `Pending → Scheduled`. During the window between the `SaveAsync` commit and the CAS commit, the arming poller can pick up the `Pending` row and also call the scheduler — two registrations for the same job.

**Fix:** When `directly: true`, save the row as `Status = Scheduled` from the start (the arming poller only queries `Pending`/`Retrying` rows, so it will never touch it). `ArmNowAsync` just calls the scheduler; on failure it rolls back `Scheduled → Pending` so the arming poller acts as the backstop.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs`
- Modify: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceTransactionalEnqueueTests.cs`

---

- [ ] **Step 1.1: Write failing tests for the new directly behavior**

Add these tests to `BackgroundJobServiceTransactionalEnqueueTests.cs`, **replacing** the two existing `directly` tests:

```csharp
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
    // Scheduler is called exactly once. No Pending→Scheduled CAS transition needed.
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
```

Note: `ThrowsAsync` comes from NSubstitute. Add `using NSubstitute.ExceptionExtensions;` at the top of the file if not already present.

- [ ] **Step 1.2: Run tests to verify they fail**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests \
  --filter "FullyQualifiedName~BackgroundJobServiceTransactionalEnqueueTests" \
  --logger "console;verbosity=normal"
```

Expected: The three new tests FAIL (old behavior). The two `Directly` tests they replace may no longer exist yet — the others (non-directly tests) should still PASS.

- [ ] **Step 1.3: Update `EnqueueAsync` — save as `Scheduled` when `directly: true`**

In `BackgroundJobService.cs`, find the `BackgroundJobInfo` construction block (around line 139) and change:

```csharp
// BEFORE:
var jobInfo = new BackgroundJobInfo(effectiveJobId, handlerName, jobName)
{
    ExpressionValue = schedule,
    Payload = eventSerializer.SerializeToElement(envelope),
    Status = BackgroundJobStatus.Pending,
    Kind = effectiveKind,
    MaxRetryCount = options.MaxRetryCount,
    NextRetryAt = clock.UtcNow,
    ExtraProperties = extraProperties
};
```

```csharp
// AFTER:
var jobInfo = new BackgroundJobInfo(effectiveJobId, handlerName, jobName)
{
    ExpressionValue = schedule,
    Payload = eventSerializer.SerializeToElement(envelope),
    // When directly:true, save as Scheduled so the arming poller cannot race and double-schedule.
    // ArmNowAsync calls the scheduler; on failure it rolls Scheduled→Pending for the poller to retry.
    Status = directly ? BackgroundJobStatus.Scheduled : BackgroundJobStatus.Pending,
    Kind = effectiveKind,
    MaxRetryCount = options.MaxRetryCount,
    NextRetryAt = clock.UtcNow,
    ExtraProperties = extraProperties
};
```

- [ ] **Step 1.4: Rewrite `ArmNowAsync`**

Replace the entire `ArmNowAsync` method (lines 204–221) with:

```csharp
/// <summary>
/// Calls the scheduler inline and, on failure, rolls the row back from Scheduled to Pending so the
/// arming poller acts as the backstop. Never throws — failure is logged as a warning.
/// </summary>
private async Task ArmNowAsync(string handlerName, string jobName, string schedule,
    ReadOnlyMemory<byte> payloadBytes, JobScheduleFailurePolicy? failurePolicy, Guid jobId, CancellationToken ct)
{
    try
    {
        await jobScheduler.ScheduleAsync(handlerName, jobName, schedule, payloadBytes, failurePolicy, ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex,
            "Immediate (directly) arm failed for job '{JobName}'; rolling back to Pending for arming poller",
            jobName);
        try
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            await jobStore.TryTransitionStatusAsync(jobId, BackgroundJobStatus.Scheduled, BackgroundJobStatus.Pending, ct);
            await uow.CommitAsync(ct);
        }
        catch (Exception rollbackEx)
        {
            logger.LogError(rollbackEx,
                "Failed to roll back job '{JobName}' to Pending; arming poller will arm it on next visibility-timeout pass",
                jobName);
        }
    }
}
```

- [ ] **Step 1.5: Remove the old directly tests (replaced in Step 1.1)**

Delete the following two test methods from `BackgroundJobServiceTransactionalEnqueueTests.cs`:
- `EnqueueAsync_Directly_NoAmbient_ArmsImmediatelyAfterCommit`
- `EnqueueAsync_Ambient_Directly_DefersArmToOnCompleted`

(They were replaced by the three new tests added in Step 1.1.)

- [ ] **Step 1.6: Run all tests to verify they pass**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests \
  --filter "FullyQualifiedName~BackgroundJobServiceTransactionalEnqueueTests" \
  --logger "console;verbosity=normal"
```

Expected output: 6 tests, all PASS (3 original non-directly tests + 3 new directly tests).

- [ ] **Step 1.7: Build to verify no compilation errors**

```bash
dotnet build framework/BBT.Aether.slnx --configuration Debug
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 1.8: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceTransactionalEnqueueTests.cs
git commit -m "fix(jobs): save directly-armed jobs as Scheduled to prevent arming-poller double-schedule race"
```

---

## Task 2: UoW awareness for `UpdateAsync` and `DeleteAsync`

**Problem:** Both methods always call `uowManager.Begin(...)` which opens a new independent transaction. When a caller has an ambient UoW (e.g., inside a business operation that also calls `EnqueueAsync`), the job update/delete commits in a separate transaction — either prematurely or without participating in the caller's rollback.

**Fix:** Check `uowManager.Current` first (exactly like `EnqueueAsync` does). If an ambient UoW exists, do the DB work inside it without committing. Otherwise open a standalone `RequiresNew + IsTransactional` UoW as before.

Note: `DeleteAsync` currently calls `uowManager.Begin()` without `RequiresNew`/`IsTransactional` options — fix that in the standalone path too.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceUpdateDeleteTests.cs`

---

- [ ] **Step 2.1: Write failing tests in a new file**

Create `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceUpdateDeleteTests.cs`:

```csharp
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
```

- [ ] **Step 2.2: Run tests to verify they fail**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests \
  --filter "FullyQualifiedName~BackgroundJobServiceUpdateDeleteTests" \
  --logger "console;verbosity=normal"
```

Expected: 7 tests, all FAIL (current `UpdateAsync`/`DeleteAsync` always open their own UoW).

- [ ] **Step 2.3: Add ambient UoW path to `UpdateAsync`**

In `BackgroundJobService.cs`, replace the `UpdateAsync` method body (keeping the guard clauses and telemetry setup) with this pattern — insert the ambient block BEFORE the `await using var uow = ...` line:

```csharp
/// <inheritdoc/>
public async Task UpdateAsync(Guid id, string newSchedule, CancellationToken cancellationToken = default)
{
    if (id == Guid.Empty)
        throw new ArgumentException("Id cannot be empty.", nameof(id));

    if (string.IsNullOrWhiteSpace(newSchedule))
        throw new ArgumentNullException(nameof(newSchedule));

    using var activity = InfrastructureActivitySource.Source.StartActivity(
        "BackgroundJob.Update",
        ActivityKind.Producer,
        Activity.Current?.Context ?? default);

    activity?.SetTag("job.id", id.ToString());
    activity?.SetTag("job.schedule", newSchedule);

    logger.LogInformation("Updating job with entity id '{Id}' to new schedule '{NewSchedule}'", id, newSchedule);

    if (uowManager.Current is { } ambient)
    {
        var jobInfo = await jobStore.GetAsync(id, cancellationToken);
        if (jobInfo == null)
            throw new InvalidOperationException($"Job with id '{id}' not found.");

        activity?.SetTag("job.handler_name", jobInfo.HandlerName);
        activity?.SetTag("job.name", jobInfo.JobName);
        jobInfo.ExpressionValue = newSchedule;
        jobInfo.Kind = InferKind(newSchedule);
        jobInfo.Status = BackgroundJobStatus.Pending;
        jobInfo.NextRetryAt = clock.UtcNow;
        await jobStore.SaveAsync(jobInfo, cancellationToken);
        logger.LogInformation("Successfully updated job with entity id '{Id}'", id);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return;
    }

    await using var uow = uowManager.Begin(
        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
    try
    {
        var jobInfo = await jobStore.GetAsync(id, cancellationToken);
        if (jobInfo == null)
            throw new InvalidOperationException($"Job with id '{id}' not found.");

        activity?.SetTag("job.handler_name", jobInfo.HandlerName);
        activity?.SetTag("job.name", jobInfo.JobName);
        jobInfo.ExpressionValue = newSchedule;
        jobInfo.Kind = InferKind(newSchedule);
        jobInfo.Status = BackgroundJobStatus.Pending;
        jobInfo.NextRetryAt = clock.UtcNow;
        await jobStore.SaveAsync(jobInfo, cancellationToken);
        await uow.CommitAsync(cancellationToken);
        logger.LogInformation("Successfully updated job with entity id '{Id}'", id);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to update job with entity id '{Id}'", id);
        RecordException(activity, ex);
        await uow.RollbackAsync(cancellationToken);
        throw;
    }
}
```

- [ ] **Step 2.4: Add ambient UoW path to `DeleteAsync`**

Replace the `DeleteAsync` method body. Key changes: add ambient check; fix standalone path to use `RequiresNew + IsTransactional` (not bare `Begin()`):

```csharp
/// <inheritdoc/>
public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
{
    if (id == Guid.Empty)
        throw new ArgumentException("Id cannot be empty.", nameof(id));

    using var activity = InfrastructureActivitySource.Source.StartActivity(
        "BackgroundJob.Delete",
        ActivityKind.Producer,
        Activity.Current?.Context ?? default);

    activity?.SetTag("job.id", id.ToString());

    logger.LogInformation("Deleting job with entity id '{Id}'", id);

    if (uowManager.Current is { })
    {
        var jobInfo = await jobStore.GetAsync(id, cancellationToken);
        if (jobInfo == null)
        {
            logger.LogWarning("Job with entity id '{Id}' not found", id);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return false;
        }

        activity?.SetTag("job.handler_name", jobInfo.HandlerName);
        activity?.SetTag("job.name", jobInfo.JobName);
        await jobScheduler.DeleteAsync(jobInfo.HandlerName, jobInfo.JobName, cancellationToken);
        await jobStore.UpdateStatusAsync(id, BackgroundJobStatus.Cancelled, clock.UtcNow,
            cancellationToken: cancellationToken);
        logger.LogInformation("Successfully deleted job with entity id '{Id}'", id);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return true;
    }

    await using var uow = uowManager.Begin(
        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
    try
    {
        var jobInfo = await jobStore.GetAsync(id, cancellationToken);
        if (jobInfo == null)
        {
            logger.LogWarning("Job with entity id '{Id}' not found", id);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return false;
        }

        activity?.SetTag("job.handler_name", jobInfo.HandlerName);
        activity?.SetTag("job.name", jobInfo.JobName);
        await jobScheduler.DeleteAsync(jobInfo.HandlerName, jobInfo.JobName, cancellationToken);
        await jobStore.UpdateStatusAsync(id, BackgroundJobStatus.Cancelled, clock.UtcNow,
            cancellationToken: cancellationToken);
        await uow.CommitAsync(cancellationToken);
        logger.LogInformation("Successfully deleted job with entity id '{Id}'", id);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to delete job with entity id '{Id}'", id);
        RecordException(activity, ex);
        await uow.RollbackAsync(cancellationToken);
        throw;
    }
}
```

- [ ] **Step 2.5: Run new tests — expect all pass**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests \
  --filter "FullyQualifiedName~BackgroundJobServiceUpdateDeleteTests" \
  --logger "console;verbosity=normal"
```

Expected: 7 tests, all PASS.

- [ ] **Step 2.6: Run full test suite — no regressions**

```bash
dotnet test framework/BBT.Aether.slnx --logger "console;verbosity=normal"
```

Expected: all tests PASS (count should equal prior run + 7 new tests).

- [ ] **Step 2.7: Build release — verify clean**

```bash
dotnet build framework/BBT.Aether.slnx --configuration Release
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 2.8: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceUpdateDeleteTests.cs
git commit -m "feat(jobs): UpdateAsync and DeleteAsync participate in ambient UoW when one is active"
```
