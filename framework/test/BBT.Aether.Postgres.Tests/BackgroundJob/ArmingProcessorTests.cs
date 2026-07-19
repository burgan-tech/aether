using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Processing;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

/// <summary>
/// Real-PostgreSQL validation of <see cref="BackgroundJobArmingProcessor"/>: that it arms the schema's
/// due jobs in the (faked) scheduler outside any transaction and atomically flips each armed row to
/// Scheduled, while leaving rows untouched when arming fails or no schema is configured. DI/schema setup
/// mirrors <see cref="JobStoreCasTests"/>.
/// </summary>
[Collection("postgres")]
public sealed class ArmingProcessorTests(PostgresFixture fx)
{
    private readonly string _schema = "jobs_" + Guid.NewGuid().ToString("N");

    private sealed class TestJobDbContext(DbContextOptions<TestJobDbContext> options)
        : AetherDbContext<TestJobDbContext>(options), IHasEfCoreBackgroundJobs
    {
        public DbSet<BackgroundJobInfo> BackgroundJobs { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureBackgroundJob();
        }
    }

    /// <summary>Hand-written recording scheduler fake. Records every call; optionally throws.</summary>
    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<(string handlerName, string jobName, string schedule)> ScheduleCalls { get; } = new();
        public List<(string handlerName, string jobName, DateTime dueAtUtc)> ScheduleOneShotCalls { get; } = new();
        public List<(string handlerName, string jobName)> DeleteCalls { get; } = new();

        public bool ThrowOnSchedule { get; set; }
        public bool ThrowOnScheduleOneShot { get; set; }

        public Task ScheduleAsync(string handlerName, string jobName, string schedule,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicyOptions = null,
            CancellationToken cancellationToken = default)
        {
            ScheduleCalls.Add((handlerName, jobName, schedule));
            if (ThrowOnSchedule)
            {
                throw new InvalidOperationException("scheduler boom");
            }

            return Task.CompletedTask;
        }

        public Task ScheduleOneShotAsync(string handlerName, string jobName, DateTime dueAtUtc,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicy = null,
            CancellationToken cancellationToken = default)
        {
            ScheduleOneShotCalls.Add((handlerName, jobName, dueAtUtc));
            if (ThrowOnScheduleOneShot)
            {
                throw new InvalidOperationException("scheduler boom");
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add((handlerName, jobName));
            return Task.CompletedTask;
        }
    }

    private sealed class GatedJobScheduler : IJobScheduler
    {
        private readonly ConcurrentDictionary<string, byte> _entries = new();
        private int _scheduleCallCount;
        private int _deleteCallCount;

        public bool GateSecondAfterCreate { get; set; }
        public int? ThrowOnDeleteCall { get; set; }
        public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);
        public TaskCompletionSource FirstScheduleEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstSchedule { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondScheduleCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSecondScheduleToReturn { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasEntry(string jobName) => _entries.ContainsKey(jobName);

        public Task ScheduleAsync(
            string handlerName,
            string jobName,
            string schedule,
            ReadOnlyMemory<byte> payload,
            JobScheduleFailurePolicy? failurePolicyOptions = null,
            CancellationToken cancellationToken = default) =>
            CompleteScheduleAsync(jobName, cancellationToken);

        public Task ScheduleOneShotAsync(
            string handlerName,
            string jobName,
            DateTime dueAtUtc,
            ReadOnlyMemory<byte> payload,
            JobScheduleFailurePolicy? failurePolicy = null,
            CancellationToken cancellationToken = default) =>
            CompleteScheduleAsync(jobName, cancellationToken);

        private async Task CompleteScheduleAsync(string jobName, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _scheduleCallCount);
            if (call == 1)
            {
                FirstScheduleEntered.TrySetResult();
                await AllowFirstSchedule.Task.WaitAsync(cancellationToken);
                _entries[jobName] = 0;
                return;
            }

            _entries[jobName] = 0;
            if (call == 2 && GateSecondAfterCreate)
            {
                SecondScheduleCreated.TrySetResult();
                await AllowSecondScheduleToReturn.Task.WaitAsync(cancellationToken);
            }
        }

        public Task DeleteAsync(
            string handlerName,
            string jobName,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _deleteCallCount);
            if (ThrowOnDeleteCall == call)
                throw new InvalidOperationException("compensating delete boom");

            _entries.TryRemove(jobName, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<BackgroundJobArmingProcessor>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));

        public bool HasWarningContaining(string text) =>
            _entries.Any(entry => entry.Level == LogLevel.Warning
                                  && entry.Message.Contains(text, StringComparison.Ordinal));
    }

    private IServiceProvider BuildProvider(FakeJobScheduler scheduler)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IJobScheduler>(scheduler);

        return services.BuildServiceProvider();
    }

    private IServiceProvider BuildProvider(IJobScheduler scheduler)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton(scheduler);

        return services.BuildServiceProvider();
    }

    private BackgroundJobArmingProcessor BuildProcessor(IServiceProvider sp, out BackgroundJobOptions options,
        string? schema)
    {
        options = new BackgroundJobOptions { Schema = schema, ArmingBatchSize = 100 };
        return new BackgroundJobArmingProcessor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IClock>(),
            options,
            NullLogger<BackgroundJobArmingProcessor>.Instance);
    }

    private BackgroundJobArmingProcessor BuildProcessor(
        IServiceProvider sp,
        BackgroundJobOptions options,
        ILogger<BackgroundJobArmingProcessor>? logger = null) =>
        new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IClock>(),
            options,
            logger ?? NullLogger<BackgroundJobArmingProcessor>.Instance);

    private async Task<bool> CancelWaitingAsync(IServiceProvider sp, Guid id)
    {
        await using var scope = sp.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
        {
            await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            var cancelled = await services.GetRequiredService<IJobStore>()
                .TryCancelWaitingAsync(id, DateTime.UtcNow);
            await uow.CommitAsync();
            return cancelled;
        }
    }

    private async Task ArrangeSchemaAsync(IServiceProvider sp)
    {
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA \"{_schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var configurator = sp.GetRequiredService<
            BBT.Aether.Uow.EntityFrameworkCore.IAetherDbContextConfigurator<TestJobDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestJobDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new BBT.Aether.Uow.EntityFrameworkCore.SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript();

        await using var ddlConn = new NpgsqlConnection(fx.ConnectionString);
        await ddlConn.OpenAsync();
        await using (var setCmd = ddlConn.CreateCommand())
        {
            setCmd.CommandText = $"SET search_path TO \"{_schema}\";";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using (var ddlCmd = ddlConn.CreateCommand())
        {
            ddlCmd.CommandText = script;
            await ddlCmd.ExecuteNonQueryAsync();
        }
    }

    private static BackgroundJobInfo NewJob(Guid id, BackgroundJobStatus status, JobKind kind = JobKind.OneShot,
        DateTime? nextRetryAt = null)
    {
        return new BackgroundJobInfo(id, "TestHandler", "job-" + id.ToString("N"))
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{\"hello\":\"world\"}").RootElement.Clone(),
            Status = status,
            Kind = kind,
            MaxRetryCount = 3,
            NextRetryAt = nextRetryAt,
        };
    }

    private async Task SeedAsync(IServiceProvider sp, params BackgroundJobInfo[] jobs)
    {
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestJobDbContext>>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            var ctx = await provider.GetDbContextAsync();
            foreach (var job in jobs)
            {
                await ctx.BackgroundJobs.AddAsync(job);
            }

            await uow.CommitAsync();
        }
    }

    private async Task<BackgroundJobInfo?> ReloadAsync(IServiceProvider sp, Guid id)
    {
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var store = ssp.GetRequiredService<IJobStore>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            var job = await store.GetAsync(id);
            await uow.CommitAsync();
            return job;
        }
    }

    [Fact]
    public async Task Arms_pending_job_and_marks_scheduled()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending, nextRetryAt: DateTime.UtcNow.AddMinutes(-1)));

        var processor = BuildProcessor(sp, out _, _schema);
        await processor.RunAsync();

        scheduler.ScheduleCalls.Count.ShouldBe(1);
        scheduler.ScheduleCalls[0].jobName.ShouldBe("job-" + id.ToString("N"));
        scheduler.ScheduleCalls[0].schedule.ShouldBe("@every 1m");
        scheduler.ScheduleOneShotCalls.ShouldBeEmpty();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
    }

    [Fact]
    public async Task Arms_due_retrying_as_oneshot()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var now = DateTime.UtcNow;
        var dueAt = now.AddMinutes(-2);
        var dueId = Guid.NewGuid();
        var futureId = Guid.NewGuid();

        await SeedAsync(sp,
            NewJob(dueId, BackgroundJobStatus.Retrying, nextRetryAt: dueAt),
            NewJob(futureId, BackgroundJobStatus.Retrying, nextRetryAt: now.AddMinutes(30)));

        var processor = BuildProcessor(sp, out _, _schema);
        await processor.RunAsync();

        scheduler.ScheduleOneShotCalls.Count.ShouldBe(1);
        scheduler.ScheduleOneShotCalls[0].jobName.ShouldBe("job-" + dueId.ToString("N"));
        scheduler.ScheduleOneShotCalls[0].dueAtUtc.ShouldBe(dueAt, TimeSpan.FromMilliseconds(1));
        scheduler.ScheduleCalls.ShouldBeEmpty();

        var reloadedDue = await ReloadAsync(sp, dueId);
        reloadedDue!.Status.ShouldBe(BackgroundJobStatus.Scheduled);

        // The future-dated retry must NOT have been armed and must stay Retrying.
        var reloadedFuture = await ReloadAsync(sp, futureId);
        reloadedFuture!.Status.ShouldBe(BackgroundJobStatus.Retrying);
    }

    [Fact]
    public async Task Arm_failure_leaves_job_pending()
    {
        var scheduler = new FakeJobScheduler { ThrowOnSchedule = true };
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending, nextRetryAt: DateTime.UtcNow.AddMinutes(-1)));

        var processor = BuildProcessor(sp, out _, _schema);

        // Must not throw out of RunAsync even though the scheduler threw.
        await processor.RunAsync();

        scheduler.ScheduleCalls.Count.ShouldBe(1);

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Pending);
    }

    [Fact]
    public async Task Two_concurrent_pods_arm_each_job_exactly_once()
    {
        // Two BackgroundJobArmingProcessor instances sharing the same PostgreSQL DB + schema simulate
        // two pods running concurrently. NpgsqlJobArmingLeaseStore uses FOR UPDATE SKIP LOCKED so each
        // pod gets a disjoint batch: no job should be armed by both pods.
        var schedulerA = new FakeJobScheduler();
        var schedulerB = new FakeJobScheduler();
        var spA = BuildProvider(schedulerA);
        var spB = BuildProvider(schedulerB);

        // ArrangeSchemaAsync creates the schema; spB uses the same connection string, so it sees the
        // same schema. Only one call to ArrangeSchemaAsync is needed — CREATE SCHEMA would fail if
        // called twice for the same name.
        await ArrangeSchemaAsync(spA);

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
        {
            var job = NewJob(id, BackgroundJobStatus.Pending,
                nextRetryAt: DateTime.UtcNow.AddMinutes(-1));
            await SeedAsync(spA, job);
        }

        var processorA = BuildProcessor(spA, out _, _schema);
        var processorB = BuildProcessor(spB, out _, _schema);

        // Both pods run concurrently; SKIP LOCKED ensures disjoint claims.
        await Task.WhenAll(processorA.RunAsync(), processorB.RunAsync());

        var allScheduledJobNames = schedulerA.ScheduleCalls.Select(c => c.jobName)
            .Concat(schedulerA.ScheduleOneShotCalls.Select(c => c.jobName))
            .Concat(schedulerB.ScheduleCalls.Select(c => c.jobName))
            .Concat(schedulerB.ScheduleOneShotCalls.Select(c => c.jobName))
            .ToList();

        allScheduledJobNames.Count.ShouldBe(4, "all 4 jobs must be armed exactly once across both pods");
        allScheduledJobNames.Distinct().Count().ShouldBe(4, "no job must be armed by both pods");

        // Verify every seeded job was armed (not just "4 unique jobs")
        var expectedNames = ids.Select(id => "job-" + id.ToString("N")).ToHashSet();
        var actualNames = allScheduledJobNames.ToHashSet();
        actualNames.SetEquals(expectedNames).ShouldBeTrue("every seeded job must be armed exactly once");
    }

    [Fact]
    public async Task Arm_completion_after_cancellation_compensates_recreated_scheduler_entry()
    {
        var scheduler = new GatedJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var job = NewJob(id, BackgroundJobStatus.Pending);
        await SeedAsync(sp, job);

        var options = new BackgroundJobOptions { Schema = _schema, ArmingBatchSize = 1 };
        var processorTask = BuildProcessor(sp, options).RunAsync();
        await scheduler.FirstScheduleEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await CancelWaitingAsync(sp, id)).ShouldBeTrue();
        await scheduler.DeleteAsync(job.HandlerName, job.JobName);

        scheduler.AllowFirstSchedule.TrySetResult();
        await processorTask;

        scheduler.HasEntry(job.JobName).ShouldBeFalse();
        scheduler.DeleteCallCount.ShouldBe(2);
        (await ReloadAsync(sp, id))!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
    }

    [Fact]
    public async Task Lost_old_claim_does_not_delete_newer_lease_owners_scheduler_entry()
    {
        var scheduler = new GatedJobScheduler { GateSecondAfterCreate = true };
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var job = NewJob(id, BackgroundJobStatus.Pending);
        await SeedAsync(sp, job);

        var expiredLeaseOptions = new BackgroundJobOptions
        {
            Schema = _schema,
            ArmingBatchSize = 1,
            ArmingLeaseDuration = TimeSpan.FromSeconds(-1)
        };
        var validLeaseOptions = new BackgroundJobOptions
        {
            Schema = _schema,
            ArmingBatchSize = 1,
            ArmingLeaseDuration = TimeSpan.FromSeconds(30)
        };
        var firstProcessor = BuildProcessor(sp, expiredLeaseOptions);
        var secondProcessor = BuildProcessor(sp, validLeaseOptions);

        var firstRun = firstProcessor.RunAsync();
        await scheduler.FirstScheduleEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondRun = secondProcessor.RunAsync();
        await scheduler.SecondScheduleCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.AllowFirstSchedule.TrySetResult();
        await firstRun;

        scheduler.HasEntry(job.JobName).ShouldBeTrue();
        scheduler.DeleteCallCount.ShouldBe(0);

        scheduler.AllowSecondScheduleToReturn.TrySetResult();
        await secondRun;

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        reloaded.ArmingToken.ShouldBeNull();
    }

    [Fact]
    public async Task Compensation_failure_is_logged_and_cancelled_state_is_preserved()
    {
        var scheduler = new GatedJobScheduler { ThrowOnDeleteCall = 2 };
        var logger = new RecordingLogger();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var job = NewJob(id, BackgroundJobStatus.Pending);
        await SeedAsync(sp, job);

        var options = new BackgroundJobOptions { Schema = _schema, ArmingBatchSize = 1 };
        var processorTask = BuildProcessor(sp, options, logger).RunAsync();
        await scheduler.FirstScheduleEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await CancelWaitingAsync(sp, id)).ShouldBeTrue();
        await scheduler.DeleteAsync(job.HandlerName, job.JobName);

        scheduler.AllowFirstSchedule.TrySetResult();
        await processorTask;

        logger.HasWarningContaining("compensating").ShouldBeTrue();
        (await ReloadAsync(sp, id))!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
    }

    [Fact]
    public async Task No_schema_skips()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending, nextRetryAt: DateTime.UtcNow.AddMinutes(-1)));

        var processor = BuildProcessor(sp, out _, schema: null);
        await processor.RunAsync();

        scheduler.ScheduleCalls.ShouldBeEmpty();
        scheduler.ScheduleOneShotCalls.ShouldBeEmpty();
        scheduler.DeleteCalls.ShouldBeEmpty();

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Pending);
    }
}
