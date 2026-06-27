using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

/// <summary>
/// Real-PostgreSQL validation of the reaping phase of <see cref="BackgroundJobArmingProcessor"/>: jobs
/// stuck in <see cref="BackgroundJobStatus.Running"/> past the configured <see
/// cref="BackgroundJobOptions.VisibilityTimeout"/> are reset for retry (one-shot → Retrying or Failed,
/// recurring → Scheduled), while fresh Running jobs are left untouched. DI/schema/fake-scheduler setup
/// mirrors <see cref="ArmingProcessorTests"/>.
/// </summary>
[Collection("postgres")]
public sealed class ReaperTests(PostgresFixture fx)
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

    /// <summary>Hand-written recording scheduler fake. Records every call; never throws.</summary>
    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<(string handlerName, string jobName, string schedule)> ScheduleCalls { get; } = new();
        public List<(string handlerName, string jobName, DateTime dueAtUtc)> ScheduleOneShotCalls { get; } = new();
        public List<(string handlerName, string jobName)> DeleteCalls { get; } = new();

        public Task ScheduleAsync(string handlerName, string jobName, string schedule,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicyOptions = null,
            CancellationToken cancellationToken = default)
        {
            ScheduleCalls.Add((handlerName, jobName, schedule));
            return Task.CompletedTask;
        }

        public Task ScheduleOneShotAsync(string handlerName, string jobName, DateTime dueAtUtc,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicy = null,
            CancellationToken cancellationToken = default)
        {
            ScheduleOneShotCalls.Add((handlerName, jobName, dueAtUtc));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add((handlerName, jobName));
            return Task.CompletedTask;
        }
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

    private BackgroundJobArmingProcessor BuildProcessor(IServiceProvider sp, out BackgroundJobOptions options,
        string? schema)
    {
        options = new BackgroundJobOptions
        {
            Schema = schema,
            ArmingBatchSize = 100,
            VisibilityTimeout = TimeSpan.FromMinutes(5),
        };
        return new BackgroundJobArmingProcessor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IClock>(),
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

    private static BackgroundJobInfo NewRunningJob(Guid id, JobKind kind, DateTime runningSince,
        int retryCount = 0, int maxRetryCount = 3, Guid? runningToken = null)
    {
        return new BackgroundJobInfo(id, "TestHandler", "job-" + id.ToString("N"))
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{\"hello\":\"world\"}").RootElement.Clone(),
            Status = BackgroundJobStatus.Running,
            Kind = kind,
            RetryCount = retryCount,
            MaxRetryCount = maxRetryCount,
            RunningSince = runningSince,
            // A Running job always carries the token stamped by its claim; the reaper transitions it out of
            // Running guarded on that token.
            RunningToken = runningToken ?? Guid.NewGuid(),
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
    public async Task Reaps_stuck_oneshot_to_retrying()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewRunningJob(id, JobKind.OneShot,
            runningSince: DateTime.UtcNow.AddMinutes(-10), retryCount: 0, maxRetryCount: 3));

        var processor = BuildProcessor(sp, out _, _schema);
        var before = DateTime.UtcNow;
        await processor.RunAsync();
        var after = DateTime.UtcNow;

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Retrying);
        reloaded.RetryCount.ShouldBe(1);
        reloaded.RunningSince.ShouldBeNull();
        reloaded.NextRetryAt.ShouldNotBeNull();
        reloaded.NextRetryAt!.Value.ShouldBeInRange(before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task Fresh_running_is_not_reaped()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var runningSince = DateTime.UtcNow.AddSeconds(-30);
        await SeedAsync(sp, NewRunningJob(id, JobKind.OneShot, runningSince: runningSince));

        var processor = BuildProcessor(sp, out _, _schema);
        await processor.RunAsync();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Running);
        reloaded.RetryCount.ShouldBe(0);
        reloaded.RunningSince.ShouldNotBeNull();
    }

    [Fact]
    public async Task Reaps_stuck_recurring_to_scheduled()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewRunningJob(id, JobKind.Recurring,
            runningSince: DateTime.UtcNow.AddMinutes(-10)));

        var processor = BuildProcessor(sp, out _, _schema);
        await processor.RunAsync();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        reloaded.RunningSince.ShouldBeNull();
    }

    [Fact]
    public async Task Reaps_exhausted_oneshot_to_failed()
    {
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewRunningJob(id, JobKind.OneShot,
            runningSince: DateTime.UtcNow.AddMinutes(-10), retryCount: 3, maxRetryCount: 3));

        var processor = BuildProcessor(sp, out _, _schema);
        await processor.RunAsync();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Failed);
        reloaded.RunningSince.ShouldBeNull();
    }

    [Fact]
    public async Task Expired_arming_claim_is_reclaimable_and_armed_in_same_pass()
    {
        // A job with an expired ArmingToken (e.g. left by a crashed pod that completed Phase 1 but
        // never reached Phase 3) must be visible to Phase 1 of the NEXT RunAsync call, because the
        // SQL claim filter includes: "ArmingToken IS NULL OR ArmingUntil < now".
        // Phase 1 will re-claim the row with a fresh token, Phase 2 arms it, Phase 3 confirms it →
        // Status = Scheduled. Reaper A then finds 0 expired claims (the confirm already cleared it).
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();

        // Build a Pending job that already has an expired arming claim from a previous (crashed) pod.
        var job = new BackgroundJobInfo(id, "TestHandler", "job-" + id.ToString("N"))
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{\"hello\":\"world\"}").RootElement.Clone(),
            Status = BackgroundJobStatus.Pending,
            Kind = JobKind.OneShot,
            MaxRetryCount = 3,
            NextRetryAt = DateTime.UtcNow.AddMinutes(-1),  // due for arming
            ArmingToken = Guid.NewGuid(),                  // stale token from crashed pod
            ArmingUntil = DateTime.UtcNow.AddSeconds(-5),  // lease has expired
        };
        await SeedAsync(sp, job);

        var processor = BuildProcessor(sp, out _, _schema);

        // Single pass: Phase 1 sees the job (ArmingUntil < now → claimable), re-claims with a new
        // token, Phase 2 arms it, Phase 3 confirms → Scheduled. Reaper A finds 0 expired claims.
        await processor.RunAsync();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled,
            "Phase 1 reclaims the expired-token row; Phase 3 confirms it as Scheduled");
        reloaded.ArmingToken.ShouldBeNull("Phase 3 clears the arming token on confirm");
        reloaded.ArmingUntil.ShouldBeNull();
        scheduler.ScheduleCalls.Count.ShouldBe(1, "the job must be armed exactly once");
        scheduler.ScheduleCalls[0].jobName.ShouldBe("job-" + id.ToString("N"));
    }

    [Fact]
    public async Task Original_completion_after_reap_does_not_stomp_retry_state()
    {
        // Regression: a slow original execution must NOT overwrite the reaper's/retry's state.
        // The reaper resets Running→Retrying (clearing the token), so the original's outcome record —
        // guarded on its now-stale token — must be a no-op (0 rows), leaving the Retrying state intact.
        var scheduler = new FakeJobScheduler();
        var sp = BuildProvider(scheduler);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var tokenA = Guid.NewGuid();
        await SeedAsync(sp, NewRunningJob(id, JobKind.OneShot,
            runningSince: DateTime.UtcNow.AddMinutes(-10), retryCount: 0, maxRetryCount: 3, runningToken: tokenA));

        // The reaper observes the stale Running job and resets it to Retrying (clears RunningToken).
        var processor = BuildProcessor(sp, out _, _schema);
        await processor.RunAsync();

        var afterReap = await ReloadAsync(sp, id);
        afterReap.ShouldNotBeNull();
        afterReap!.Status.ShouldBe(BackgroundJobStatus.Retrying);
        afterReap.RetryCount.ShouldBe(1);
        afterReap.RunningToken.ShouldBeNull();

        // The slow original execution finally finishes and tries to record Completed under tokenA.
        bool recorded;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
            var store = ssp.GetRequiredService<IJobStore>();

            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                recorded = await store.TryRecordTerminalAsync(id, tokenA, BackgroundJobStatus.Completed,
                    DateTime.UtcNow, null, default);
                await uow.CommitAsync();
            }
        }

        recorded.ShouldBeFalse("the original's stale token must not record the outcome");

        var final = await ReloadAsync(sp, id);
        final.ShouldNotBeNull();
        final!.Status.ShouldBe(BackgroundJobStatus.Retrying, "the reaper's retry state must survive");
        final.RetryCount.ShouldBe(1, "retry must increment exactly once per claim");
    }
}
