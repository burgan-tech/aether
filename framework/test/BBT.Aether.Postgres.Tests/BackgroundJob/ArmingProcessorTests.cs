using System;
using System.Collections.Generic;
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
        options = new BackgroundJobOptions { Schema = schema, ArmingBatchSize = 100 };
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
