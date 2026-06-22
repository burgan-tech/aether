using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Dapr;
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

/// <summary>
/// Real-PostgreSQL end-to-end coverage of the WHOLE refactored background-job flow on a single path:
/// <c>EnqueueAsync</c> (writes a Pending row, no scheduler call) → <see cref="BackgroundJobArmingProcessor"/>
/// (arms the due job in the scheduler and flips it Pending→Scheduled) → a simulated Dapr fire →
/// <see cref="DaprJobExecutionBridge"/> (looks the job up in its own UoW) → <see cref="JobDispatcher"/>
/// (CAS-claims Scheduled→Running, runs the handler, records the outcome).
///
/// The Dapr trigger is simulated deterministically: a recording <see cref="FakeJobScheduler"/> captures the
/// exact payload bytes the arming processor would have handed to Dapr, and the TEST then calls
/// <c>bridge.ExecuteAsync(jobName, capturedBytes, ct)</c> itself — so the bridge receives byte-for-byte what
/// Dapr would have delivered, but the test keeps full control over WHEN the fire happens (no background loop,
/// no real Dapr). All real components (service, poller, bridge, dispatcher, store, EF/UoW) are wired through
/// DI exactly as production wires them; only the scheduler is faked.
///
/// The retry cycle advances "time" without a controllable clock by patching <c>NextRetryAt</c> into the past
/// via raw SQL between cycles, so the arming poller's due-query (<c>NextRetryAt &lt;= now</c>) re-arms the
/// Retrying one-shot. DI/schema/seed setup mirrors <see cref="JobDispatcherTests"/> and
/// <see cref="ArmingProcessorTests"/>.
/// </summary>
[Collection("postgres")]
public sealed class EndToEndJobLifecycleTests(PostgresFixture fx)
{
    private const string HandlerName = "TestHandler";
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

    /// <summary>Simple test args payload.</summary>
    private sealed class TestArgs
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Controllable test handler driven by a static switch; records its invocation count so each test can
    /// prove the handler ran (and how many times — e.g. a recurring re-fire runs it twice).
    /// </summary>
    private sealed class TestHandler : IBackgroundJobHandler<TestArgs>
    {
        public static bool ShouldThrow { get; set; }
        public static int InvocationCount { get; set; }

        public static void Reset()
        {
            ShouldThrow = false;
            InvocationCount = 0;
        }

        public Task HandleAsync(TestArgs args, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (ShouldThrow)
            {
                throw new InvalidOperationException("handler boom");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test invoker mirroring the framework's internal BackgroundJobInvoker&lt;TArgs&gt;: resolves the handler
    /// from the dispatch scope, deserializes the args, invokes it. Placed directly into <c>options.Invokers</c>.
    /// </summary>
    private sealed class TestInvoker : IBackgroundJobInvoker
    {
        public async Task InvokeAsync(IServiceProvider serviceProvider, IEventSerializer eventSerializer,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var handler = serviceProvider.GetRequiredService<IBackgroundJobHandler<TestArgs>>();
            var args = eventSerializer.Deserialize<TestArgs>(payload.Span)
                       ?? throw new InvalidOperationException("Failed to deserialize TestArgs");
            await handler.HandleAsync(args, cancellationToken);
        }
    }

    /// <summary>
    /// Recording scheduler fake that is the lynchpin of the E2E simulation. It does NOT auto-fire — instead it
    /// captures, per job name, the exact payload bytes the arming processor passed (the same bytes Dapr would
    /// deliver back to the trigger endpoint). The test then drives <c>bridge.ExecuteAsync</c> with those bytes
    /// to simulate the Dapr callback, keeping full control over timing. Schedule/one-shot/delete calls are all
    /// recorded so the test can assert the arming + scheduler-cleanup behavior.
    /// </summary>
    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<(string handlerName, string jobName, string schedule)> ScheduleCalls { get; } = new();
        public List<(string handlerName, string jobName, DateTime dueAtUtc)> ScheduleOneShotCalls { get; } = new();
        public List<(string handlerName, string jobName)> DeleteCalls { get; } = new();

        /// <summary>The most recent payload bytes captured for each job name (what Dapr would re-deliver).</summary>
        public Dictionary<string, byte[]> CapturedPayloads { get; } = new();

        public Task ScheduleAsync(string handlerName, string jobName, string schedule,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicyOptions = null,
            CancellationToken cancellationToken = default)
        {
            ScheduleCalls.Add((handlerName, jobName, schedule));
            CapturedPayloads[jobName] = payload.ToArray();
            return Task.CompletedTask;
        }

        public Task ScheduleOneShotAsync(string handlerName, string jobName, DateTime dueAtUtc,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicy = null,
            CancellationToken cancellationToken = default)
        {
            ScheduleOneShotCalls.Add((handlerName, jobName, dueAtUtc));
            CapturedPayloads[jobName] = payload.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add((handlerName, jobName));
            return Task.CompletedTask;
        }
    }

    private BackgroundJobOptions BuildOptions(int maxRetryCount = 3)
    {
        var options = new BackgroundJobOptions { Schema = _schema, MaxRetryCount = maxRetryCount, ArmingBatchSize = 100 };
        // Mirror AddAetherBackgroundJob's invoker creation (closes the generic at "startup").
        options.Invokers[HandlerName] = new TestInvoker();
        return options;
    }

    private IServiceProvider BuildProvider(FakeJobScheduler scheduler, BackgroundJobOptions options)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IJobScheduler>(scheduler);
        services.AddSingleton(options);
        // Real production wiring for the full path under test.
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddScoped<IJobDispatcher, JobDispatcher>();
        services.AddScoped<IJobExecutionBridge, DaprJobExecutionBridge>();
        // The handler itself, resolved by the invoker from the dispatch scope.
        services.AddScoped<IBackgroundJobHandler<TestArgs>, TestHandler>();

        return services.BuildServiceProvider();
    }

    private BackgroundJobArmingProcessor BuildArmingProcessor(IServiceProvider sp, BackgroundJobOptions options)
    {
        return new BackgroundJobArmingProcessor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IEventSerializer>(),
            options,
            NullLogger<BackgroundJobArmingProcessor>.Instance);
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

    /// <summary>Enqueues a job through the REAL service while a schema scope is active.</summary>
    private async Task<Guid> EnqueueAsync(IServiceProvider sp, string jobName, string schedule)
    {
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        using (currentSchema.Change(_schema))
        {
            var svc = ssp.GetRequiredService<IBackgroundJobService>();
            return await svc.EnqueueAsync(HandlerName, jobName, new TestArgs { Value = "x" }, schedule);
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

    /// <summary>
    /// Patches a job's MaxRetryCount via raw SQL (used to make the one-shot exhaust retries quickly without a
    /// custom options/seed path — the job is created by the real EnqueueAsync from the configured options).
    /// </summary>
    private async Task SetMaxRetryCountAsync(Guid id, int maxRetryCount)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE \"{_schema}\".\"BackgroundJobs\" SET \"MaxRetryCount\" = @m WHERE \"Id\" = @id;";
        cmd.Parameters.AddWithValue("m", maxRetryCount);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Advances "time" for the retry cycle: pulls NextRetryAt into the past so the arming poller's due-query
    /// (Retrying AND NextRetryAt &lt;= now) picks the job up on the next pass. No controllable clock needed.
    /// </summary>
    private async Task PullNextRetryIntoPastAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE \"{_schema}\".\"BackgroundJobs\" SET \"NextRetryAt\" = @t WHERE \"Id\" = @id;";
        cmd.Parameters.AddWithValue("t", DateTime.UtcNow.AddMinutes(-5));
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task OneShot_completes_end_to_end()
    {
        TestHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // 1) Enqueue a one-shot job (ISO-8601 instant ⇒ OneShot). No scheduler call yet.
        var jobName = "e2e-oneshot-" + Guid.NewGuid().ToString("N");
        var id = await EnqueueAsync(sp, jobName, "2099-01-01T00:00:00Z");

        var afterEnqueue = await ReloadAsync(sp, id);
        afterEnqueue!.Status.ShouldBe(BackgroundJobStatus.Pending);
        afterEnqueue.Kind.ShouldBe(JobKind.OneShot);
        scheduler.ScheduleCalls.ShouldBeEmpty();
        scheduler.ScheduleOneShotCalls.ShouldBeEmpty();

        // 2) Arm: poller arms the Pending job via ScheduleAsync (one-shot scheduling is reserved for due
        // Retrying jobs) and flips Pending→Scheduled.
        await BuildArmingProcessor(sp, options).RunAsync();

        scheduler.ScheduleCalls.Count.ShouldBe(1);
        scheduler.ScheduleCalls[0].jobName.ShouldBe(jobName);
        var armed = await ReloadAsync(sp, id);
        armed!.Status.ShouldBe(BackgroundJobStatus.Scheduled);

        // 3) Simulate the Dapr fire: feed the captured payload bytes into the bridge.
        scheduler.CapturedPayloads.ShouldContainKey(jobName);
        await using (var scope = sp.CreateAsyncScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IJobExecutionBridge>();
            await bridge.ExecuteAsync(jobName, scheduler.CapturedPayloads[jobName], CancellationToken.None);
        }

        // 4) Outcome: handler ran once, job Completed, scheduler entry deleted.
        TestHandler.InvocationCount.ShouldBe(1);
        var done = await ReloadAsync(sp, id);
        done!.Status.ShouldBe(BackgroundJobStatus.Completed);
        scheduler.DeleteCalls.Count.ShouldBe(1);
        scheduler.DeleteCalls[0].jobName.ShouldBe(jobName);
    }

    [Fact]
    public async Task Recurring_stays_scheduled_and_can_fire_again()
    {
        TestHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // 1) Enqueue a recurring job (cron ⇒ Recurring).
        var jobName = "e2e-recurring-" + Guid.NewGuid().ToString("N");
        var id = await EnqueueAsync(sp, jobName, "*/5 * * * *");
        (await ReloadAsync(sp, id))!.Kind.ShouldBe(JobKind.Recurring);

        // 2) Arm: recurring uses ScheduleAsync with the cron expression.
        await BuildArmingProcessor(sp, options).RunAsync();
        scheduler.ScheduleCalls.Count.ShouldBe(1);
        scheduler.ScheduleCalls[0].schedule.ShouldBe("*/5 * * * *");
        (await ReloadAsync(sp, id))!.Status.ShouldBe(BackgroundJobStatus.Scheduled);

        // 3) First Dapr fire: recurring stays Scheduled, LastRunAt set, NO delete.
        await using (var scope = sp.CreateAsyncScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IJobExecutionBridge>();
            await bridge.ExecuteAsync(jobName, scheduler.CapturedPayloads[jobName], CancellationToken.None);
        }

        TestHandler.InvocationCount.ShouldBe(1);
        var afterFirst = await ReloadAsync(sp, id);
        afterFirst!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        afterFirst.LastRunAt.ShouldNotBeNull();
        scheduler.DeleteCalls.ShouldBeEmpty();

        // 4) Second Dapr fire (still Scheduled, still armed): it runs again.
        await using (var scope = sp.CreateAsyncScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IJobExecutionBridge>();
            await bridge.ExecuteAsync(jobName, scheduler.CapturedPayloads[jobName], CancellationToken.None);
        }

        TestHandler.InvocationCount.ShouldBe(2);
        var afterSecond = await ReloadAsync(sp, id);
        afterSecond!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneShot_failure_retries_then_fails()
    {
        TestHandler.Reset();
        TestHandler.ShouldThrow = true; // every invocation throws
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // 1) Enqueue a one-shot job; cap retries at 1 so the cycle is short.
        var jobName = "e2e-retry-" + Guid.NewGuid().ToString("N");
        var id = await EnqueueAsync(sp, jobName, "2099-01-01T00:00:00Z");
        await SetMaxRetryCountAsync(id, 1);

        // 2) First cycle: arm (Pending → ScheduleAsync) → fire → fails with retries left → Retrying
        // (RetryCount=1, NextRetryAt set).
        await BuildArmingProcessor(sp, options).RunAsync();
        scheduler.ScheduleCalls.Count.ShouldBe(1);
        (await ReloadAsync(sp, id))!.Status.ShouldBe(BackgroundJobStatus.Scheduled);

        await using (var scope = sp.CreateAsyncScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IJobExecutionBridge>();
            await bridge.ExecuteAsync(jobName, scheduler.CapturedPayloads[jobName], CancellationToken.None);
        }

        TestHandler.InvocationCount.ShouldBe(1);
        var retrying = await ReloadAsync(sp, id);
        retrying!.Status.ShouldBe(BackgroundJobStatus.Retrying);
        retrying.RetryCount.ShouldBe(1);
        retrying.NextRetryAt.ShouldNotBeNull();
        retrying.LastError.ShouldNotBeNull();
        scheduler.DeleteCalls.ShouldBeEmpty(); // not deleted — poller re-arms it

        // 3) Advance time so the Retrying job is due, then re-arm (Retrying→Scheduled via one-shot).
        await PullNextRetryIntoPastAsync(id);
        await BuildArmingProcessor(sp, options).RunAsync();
        scheduler.ScheduleOneShotCalls.Count.ShouldBe(1); // due Retrying job re-armed as a one-shot at NextRetryAt
        (await ReloadAsync(sp, id))!.Status.ShouldBe(BackgroundJobStatus.Scheduled);

        // 4) Second fire: retries exhausted (retryCount 1, max 1) → Failed + scheduler delete.
        await using (var scope = sp.CreateAsyncScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IJobExecutionBridge>();
            await bridge.ExecuteAsync(jobName, scheduler.CapturedPayloads[jobName], CancellationToken.None);
        }

        TestHandler.InvocationCount.ShouldBe(2);
        var failed = await ReloadAsync(sp, id);
        failed!.Status.ShouldBe(BackgroundJobStatus.Failed);
        failed.LastError.ShouldNotBeNull();
        scheduler.DeleteCalls.Count.ShouldBe(1);
        scheduler.DeleteCalls[0].jobName.ShouldBe(jobName);
    }
}
