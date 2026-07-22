using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

/// <summary>
/// Real-PostgreSQL validation of the atomic claim (<see cref="IJobStore.TryClaimAsync"/>, which sets
/// Status=Running and stamps RunningSince in one conditional UPDATE) and the visibility-timeout reaper
/// query (<see cref="IJobStore.GetStaleRunningAsync"/>). Mirrors JobStoreCasTests: a real DI container,
/// a test <see cref="IHasEfCoreBackgroundJobs"/> context, and a GUID-suffixed schema created via EF
/// Core's GenerateCreateScript, exercised inside the multi-schema UnitOfWork's shared transaction.
/// </summary>
[Collection("postgres")]
public sealed class JobStoreClaimReaperTests(PostgresFixture fx)
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

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
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
        var script = ctx.Database.GenerateCreateScript()
            .Replace("\"__aether_schema__\"", $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace("__aether_schema__", $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace($"CREATE SCHEMA \"{_schema}\";", $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\";", StringComparison.Ordinal);

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

    private static BackgroundJobInfo NewJob(Guid id, BackgroundJobStatus status, DateTime? runningSince = null)
    {
        return new BackgroundJobInfo(id, "TestHandler", "job-" + id.ToString("N"))
        {
            Payload = JsonDocument.Parse("{}").RootElement,
            Status = status,
            Kind = JobKind.OneShot,
            MaxRetryCount = 3,
            RunningSince = runningSince,
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
    public async Task TryClaim_only_one_of_two_concurrent_winners()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

        var now = DateTime.UtcNow;

        // Each task runs in its OWN scope + Change + UoW, so both genuinely race on the atomic
        // conditional UPDATE. The WHERE Status=Scheduled guard means exactly one row update succeeds.
        async Task<bool> ClaimAsync()
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
                var won = await store.TryClaimAsync(id, now, Guid.NewGuid());
                await uow.CommitAsync();
                return won;
            }
        }

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync());

        results.Count(r => r).ShouldBe(1, "exactly one concurrent claim must win");
        results.Count(r => !r).ShouldBe(1, "exactly one concurrent claim must lose");

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Running);
        reloaded.RunningSince.ShouldNotBeNull();
        reloaded.RunningSince!.Value.ShouldBe(now, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task TryClaim_fails_when_not_scheduled()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Running, runningSince: DateTime.UtcNow.AddMinutes(-1)));

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var store = ssp.GetRequiredService<IJobStore>();

        bool won;
        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            won = await store.TryClaimAsync(id, DateTime.UtcNow, Guid.NewGuid());
            await uow.CommitAsync();
        }

        won.ShouldBeFalse("a job that is not Scheduled cannot be claimed");
    }

    [Theory]
    [InlineData(BackgroundJobStatus.Pending)]
    [InlineData(BackgroundJobStatus.Scheduled)]
    [InlineData(BackgroundJobStatus.Retrying)]
    public async Task TryCancelWaiting_cancels_waiting_status(BackgroundJobStatus initial)
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);
        var id = Guid.NewGuid();
        var handledAt = DateTime.UtcNow;
        await SeedAsync(sp, NewJob(id, initial));

        await RunInUowAsync(sp, async store =>
            (await store.TryCancelWaitingAsync(id, handledAt)).ShouldBeTrue());

        var job = await ReloadAsync(sp, id);
        job!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
        job.HandledTime.ShouldNotBeNull();
        job.HandledTime.Value.ShouldBe(handledAt, TimeSpan.FromSeconds(1));
        job.RunningSince.ShouldBeNull();
        job.RunningToken.ShouldBeNull();
        job.ArmingToken.ShouldBeNull();
        job.ArmingUntil.ShouldBeNull();
    }

    [Fact]
    public async Task TryCancelWaiting_preserves_running_claim()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

        await RunInUowAsync(sp, async store =>
        {
            (await store.TryClaimAsync(id, DateTime.UtcNow, token)).ShouldBeTrue();
            (await store.TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeFalse();
        });

        var job = await ReloadAsync(sp, id);
        job!.Status.ShouldBe(BackgroundJobStatus.Running);
        job.RunningToken.ShouldBe(token);
        job.RunningSince.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(BackgroundJobStatus.Completed)]
    [InlineData(BackgroundJobStatus.Failed)]
    [InlineData(BackgroundJobStatus.Cancelled)]
    public async Task TryCancelWaiting_preserves_terminal_status(BackgroundJobStatus initial)
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);
        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, initial));

        await RunInUowAsync(sp, async store =>
            (await store.TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeFalse());

        (await ReloadAsync(sp, id))!.Status.ShouldBe(initial);
    }

    [Theory]
    [InlineData(BackgroundJobStatus.Running)]
    [InlineData(BackgroundJobStatus.Completed)]
    public async Task Cancellation_snapshot_bypasses_tracked_waiting_entity_after_concurrent_transition(
        BackgroundJobStatus concurrentStatus)
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);
        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending));

        await using var scope = sp.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var schema = services.GetRequiredService<ICurrentSchema>().Change(_schema);
        await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
            new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            });
        var store = services.GetRequiredService<IJobStore>();
        var tracked = await store.GetAsync(id);
        tracked.ShouldNotBeNull();
        tracked!.Status.ShouldBe(BackgroundJobStatus.Pending);

        await RunInUowAsync(sp, concurrentStore =>
            concurrentStore.UpdateStatusAsync(id, concurrentStatus, DateTime.UtcNow));

        (await store.TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeFalse();
        tracked.Status.ShouldBe(BackgroundJobStatus.Pending,
            "ExecuteUpdate and the concurrent UoW do not refresh the first context's tracked entity");

        var snapshot = await store.GetCancellationSnapshotAsync(id);
        snapshot.ShouldNotBeNull();
        snapshot!.Status.ShouldBe(concurrentStatus);
        await uow.CommitAsync();
    }

    [Fact]
    public async Task Claim_and_waiting_cancellation_have_exactly_one_winner()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);
        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

        var results = await Task.WhenAll(
            RunInNewUowAsync(sp, store =>
                store.TryClaimAsync(id, DateTime.UtcNow, Guid.NewGuid())),
            RunInNewUowAsync(sp, store =>
                store.TryCancelWaitingAsync(id, DateTime.UtcNow)));

        results.Count(won => won).ShouldBe(1);
        var status = (await ReloadAsync(sp, id))!.Status;
        new[] { BackgroundJobStatus.Running, BackgroundJobStatus.Cancelled }
            .ShouldContain(status);
    }

    [Fact]
    public async Task GetStaleRunning_returns_only_timed_out()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var now = DateTime.UtcNow;
        var staleId = Guid.NewGuid();
        var freshId = Guid.NewGuid();

        await SeedAsync(sp,
            NewJob(staleId, BackgroundJobStatus.Running, runningSince: now.AddMinutes(-10)),
            NewJob(freshId, BackgroundJobStatus.Running, runningSince: now.AddSeconds(-10)));

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var store = ssp.GetRequiredService<IJobStore>();

        IReadOnlyList<BackgroundJobInfo> stale;
        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            stale = await store.GetStaleRunningAsync(now.AddMinutes(-5), 10);
            await uow.CommitAsync();
        }

        var ids = stale.Select(j => j.Id).ToList();
        ids.ShouldContain(staleId);
        ids.ShouldNotContain(freshId);
    }

    [Fact]
    public async Task TryRecordTerminal_only_the_current_token_wins_exactly_once()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

        var realToken = Guid.NewGuid();
        var staleToken = Guid.NewGuid();

        // Claim the job, stamping the real token.
        await RunInUowAsync(sp, async store =>
        {
            var claimed = await store.TryClaimAsync(id, DateTime.UtcNow, realToken);
            claimed.ShouldBeTrue();
        });

        // A holder of a stale token (e.g. a reaper that observed a different lease) must lose.
        await RunInUowAsync(sp, async store =>
        {
            var lost = await store.TryRecordTerminalAsync(id, staleToken, BackgroundJobStatus.Completed,
                DateTime.UtcNow, null, default);
            lost.ShouldBeFalse("a stale token cannot record the outcome");
        });

        // The job is still Running under the real token.
        var midway = await ReloadAsync(sp, id);
        midway.ShouldNotBeNull();
        midway!.Status.ShouldBe(BackgroundJobStatus.Running);

        // The holder of the current token wins.
        await RunInUowAsync(sp, async store =>
        {
            var won = await store.TryRecordTerminalAsync(id, realToken, BackgroundJobStatus.Completed,
                DateTime.UtcNow, null, default);
            won.ShouldBeTrue("the current token must record the outcome");
        });

        var final = await ReloadAsync(sp, id);
        final.ShouldNotBeNull();
        final!.Status.ShouldBe(BackgroundJobStatus.Completed);
        final.RunningSince.ShouldBeNull();
        final.RunningToken.ShouldBeNull();
    }

    private async Task RunInUowAsync(IServiceProvider sp, Func<IJobStore, Task> action)
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
            await action(store);
            await uow.CommitAsync();
        }
    }

    private async Task<T> RunInNewUowAsync<T>(
        IServiceProvider sp,
        Func<IJobStore, Task<T>> action)
    {
        await using var scope = sp.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var schema = services.GetRequiredService<ICurrentSchema>().Change(_schema);
        await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
            new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            });
        var result = await action(services.GetRequiredService<IJobStore>());
        await uow.CommitAsync();
        return result;
    }
}
