using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Events;
using BBT.Aether.Guids;
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
/// Real-PostgreSQL validation that <see cref="BackgroundJobService"/> after the arming-poller refactor
/// makes enqueue/update atomic and scheduler-free: enqueue writes ONLY a <see cref="BackgroundJobStatus.Pending"/>
/// row (inferring the <see cref="JobKind"/> from the schedule), the ambient path commits the row together
/// with the caller's UoW (rollback ⇒ no row), and reschedule via <c>UpdateAsync</c> hands the row back to
/// the poller (Pending + new schedule) without touching the stored payload or calling the scheduler.
/// DI/schema setup mirrors <see cref="JobDispatcherTests"/>.
/// </summary>
[Collection("postgres")]
public sealed class EnqueueAtomicityTests(PostgresFixture fx)
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

    private sealed class TestArgs
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Recording scheduler fake. Records every call; enqueue/update must NOT call it.</summary>
    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<(string handlerName, string jobName, string schedule)> ScheduleCalls { get; } = new();
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
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add((handlerName, jobName));
            return Task.CompletedTask;
        }
    }

    private BackgroundJobOptions BuildOptions() => new() { Schema = _schema, MaxRetryCount = 3 };

    private IServiceProvider BuildProvider(FakeJobScheduler scheduler, BackgroundJobOptions options)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IJobScheduler>(scheduler);
        services.AddSingleton(options);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();

        return services.BuildServiceProvider();
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

    private async Task SeedAsync(IServiceProvider sp, BackgroundJobInfo job)
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
            await ctx.BackgroundJobs.AddAsync(job);
            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task Enqueue_writes_pending_row_without_calling_scheduler()
    {
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        Guid id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(_schema))
            {
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                id = await svc.EnqueueAsync(HandlerName, "job-cron", new TestArgs { Value = "x" }, "*/5 * * * *");
            }
        }

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Pending);
        reloaded.Kind.ShouldBe(JobKind.Recurring);
        reloaded.MaxRetryCount.ShouldBe(options.MaxRetryCount);
        reloaded.NextRetryAt.ShouldNotBeNull();
        scheduler.ScheduleCalls.ShouldBeEmpty();
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Enqueue_oneshot_inference()
    {
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        Guid id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(_schema))
            {
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                id = await svc.EnqueueAsync(HandlerName, "job-oneshot", new TestArgs { Value = "x" },
                    "2026-07-01T10:00:00Z");
            }
        }

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Kind.ShouldBe(JobKind.OneShot);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Pending);
        scheduler.ScheduleCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ambient_rolls_back_with_caller()
    {
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // Case 1: ambient UoW rolled back → no row persisted (atomic with the caller). directly:false — the
        // row participates in our own UoW set as uowManager.Current.
        var rolledBackId = Guid.NewGuid();
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                await svc.EnqueueAsync(HandlerName, "job-rollback", new TestArgs { Value = "x" }, "*/5 * * * *",
                    jobId: rolledBackId);
                await uow.RollbackAsync();
            }
        }

        (await ReloadAsync(sp, rolledBackId)).ShouldBeNull();

        // Case 2: ambient UoW committed → row persists as Pending, scheduler NOT called (directly:false).
        var committedId = Guid.NewGuid();
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                await svc.EnqueueAsync(HandlerName, "job-commit", new TestArgs { Value = "x" }, "*/5 * * * *",
                    jobId: committedId);
                await uow.CommitAsync();
            }
        }

        var committed = await ReloadAsync(sp, committedId);
        committed.ShouldNotBeNull();
        committed!.Status.ShouldBe(BackgroundJobStatus.Pending);
        scheduler.ScheduleCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Directly_arms_immediately_when_no_ambient()
    {
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // No ambient UoW → standalone commit, then the directly flag arms the scheduler inline and flips
        // the row to Scheduled, without the poller running.
        Guid id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(_schema))
            {
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                id = await svc.EnqueueAsync(HandlerName, "job-directly", new TestArgs { Value = "x" },
                    "*/5 * * * *", directly: true);
            }
        }

        scheduler.ScheduleCalls.Count.ShouldBe(1);
        scheduler.ScheduleCalls[0].jobName.ShouldBe("job-directly");
        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
    }

    [Fact]
    public async Task Directly_ambient_arms_after_commit()
    {
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // Ambient + directly: arming is deferred to the ambient UoW's OnCompleted, so it must NOT fire
        // before commit, and MUST fire (and flip Scheduled) after commit.
        var id = Guid.NewGuid();
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                await svc.EnqueueAsync(HandlerName, "job-directly-ambient", new TestArgs { Value = "x" },
                    "*/5 * * * *", directly: true, jobId: id);

                // BEFORE commit: not yet armed.
                scheduler.ScheduleCalls.ShouldBeEmpty();

                await uow.CommitAsync();
            }
        }

        // AFTER commit: armed and flipped to Scheduled.
        scheduler.ScheduleCalls.Count.ShouldBe(1);
        scheduler.ScheduleCalls[0].jobName.ShouldBe("job-directly-ambient");
        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
    }

    [Fact]
    public async Task Update_sets_pending_and_new_schedule_without_scheduler()
    {
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var originalPayload = System.Text.Json.JsonDocument.Parse(
            "{\"data\":{\"Value\":\"original\"},\"schema\":\"" + _schema + "\"}").RootElement.Clone();
        await SeedAsync(sp, new BackgroundJobInfo(id, HandlerName, "job-update")
        {
            ExpressionValue = "2026-07-01T10:00:00Z",
            Payload = originalPayload,
            Status = BackgroundJobStatus.Scheduled,
            Kind = JobKind.OneShot,
            MaxRetryCount = 3,
        });

        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(_schema))
            {
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                await svc.UpdateAsync(id, "0 0 * * *");
            }
        }

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Pending);
        reloaded.ExpressionValue.ShouldBe("0 0 * * *");
        reloaded.Kind.ShouldBe(JobKind.Recurring);
        reloaded.NextRetryAt.ShouldNotBeNull();
        // Payload is untouched — still the original envelope the poller reuses. Compare on canonicalized
        // JSON since PostgreSQL's jsonb round-trip reformats insignificant whitespace.
        static string Canonical(System.Text.Json.JsonElement e) =>
            System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(e.GetRawText()));
        Canonical(reloaded.Payload).ShouldBe(Canonical(originalPayload));
        scheduler.ScheduleCalls.ShouldBeEmpty();
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Update_reschedules_a_pending_job()
    {
        // Regression for the id-based SaveAsync upsert: a Pending job is NOT matched by
        // GetByJobNameAsync (filtered to Scheduled||Running), so the old name-based upsert fell into the
        // AddAsync branch and threw on the duplicate PK. The id-based lookup finds the tracked row and the
        // reschedule succeeds for a job in ANY status.
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        Guid id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(_schema))
            {
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                // Enqueue leaves the row Pending (no poller runs in this test).
                id = await svc.EnqueueAsync(HandlerName, "job-pending-update", new TestArgs { Value = "x" },
                    "2026-07-01T10:00:00Z");
            }
        }

        (await ReloadAsync(sp, id))!.Status.ShouldBe(BackgroundJobStatus.Pending);

        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(_schema))
            {
                var svc = ssp.GetRequiredService<IBackgroundJobService>();
                await svc.UpdateAsync(id, "0 0 * * *");
            }
        }

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Pending);
        reloaded.ExpressionValue.ShouldBe("0 0 * * *");
        reloaded.Kind.ShouldBe(JobKind.Recurring);
        reloaded.NextRetryAt.ShouldNotBeNull();
        scheduler.ScheduleCalls.ShouldBeEmpty();
        scheduler.DeleteCalls.ShouldBeEmpty();
    }
}
