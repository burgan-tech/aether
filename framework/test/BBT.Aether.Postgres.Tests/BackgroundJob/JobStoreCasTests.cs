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
/// Real-PostgreSQL validation of the atomic optimistic-concurrency claim and arming queries added to
/// <see cref="IJobStore"/>. Drives a real DI container (the framework's own registration extensions)
/// with a test <see cref="IHasEfCoreBackgroundJobs"/> context, creates the schema + BackgroundJobs
/// table via EF Core's GenerateCreateScript under a GUID-suffixed schema, and exercises the store
/// inside the multi-schema UnitOfWork's shared transaction.
/// </summary>
[Collection("postgres")]
public sealed class JobStoreCasTests(PostgresFixture fx)
{
    // GUID-suffixed schema avoids cross-test contention on the shared container. The name is already
    // lowercase hex + underscores, so DefaultSchemaNameFormatter.Format leaves it unchanged.
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

        // Core: IClock, IGuidGenerator, ICurrentSchema, ISchemaNameFormatter, etc.
        services.AddAetherCore(_ => { });

        // DbContext + UnitOfWork wiring (configurator, UoW manager, ambient accessor, provider).
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);

        // The job store under test.
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates the schema, then the BackgroundJobs table using EF Core's own GenerateCreateScript()
    /// (so the DDL matches the entity shape exactly), executed under a connection whose search_path
    /// points at the test schema.
    /// </summary>
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
        var job = new BackgroundJobInfo(id, "TestHandler", "job-" + id.ToString("N"))
        {
            Payload = JsonDocument.Parse("{}").RootElement,
            Status = status,
            Kind = kind,
            MaxRetryCount = 3,
            NextRetryAt = nextRetryAt,
        };
        return job;
    }

    /// <summary>Seeds the given jobs inside their own committed UoW so they are visible to later scopes.</summary>
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
    public async Task TryTransition_only_one_of_two_concurrent_claims_wins()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

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
                var won = await store.TryTransitionStatusAsync(id, BackgroundJobStatus.Scheduled, BackgroundJobStatus.Running);
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
    }

    [Fact]
    public async Task GetDueForArming_returns_pending_and_due_retrying_only()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var now = DateTime.UtcNow;
        var pendingId = Guid.NewGuid();
        var dueRetryId = Guid.NewGuid();
        var futureRetryId = Guid.NewGuid();

        await SeedAsync(sp,
            NewJob(pendingId, BackgroundJobStatus.Pending, nextRetryAt: now.AddMinutes(-5)),
            NewJob(dueRetryId, BackgroundJobStatus.Retrying, nextRetryAt: now.AddMinutes(-1)),
            NewJob(futureRetryId, BackgroundJobStatus.Retrying, nextRetryAt: now.AddMinutes(10)));

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var store = ssp.GetRequiredService<IJobStore>();

        IReadOnlyList<BackgroundJobInfo> due;
        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            due = await store.GetDueForArmingAsync(now, 10);
            await uow.CommitAsync();
        }

        var ids = due.Select(j => j.Id).ToList();
        ids.ShouldContain(pendingId);
        ids.ShouldContain(dueRetryId);
        ids.ShouldNotContain(futureRetryId);
        // Ordered by NextRetryAt ascending: pending (-5m) before due retry (-1m).
        ids.IndexOf(pendingId).ShouldBeLessThan(ids.IndexOf(dueRetryId));
    }

    [Fact]
    public async Task MarkRetrying_sets_fields()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Running));

        var future = DateTime.UtcNow.AddMinutes(15);

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
                await store.MarkRetryingAsync(id, future, "boom");
                await uow.CommitAsync();
            }
        }

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Retrying);
        reloaded.RetryCount.ShouldBe(1);
        reloaded.NextRetryAt!.Value.ShouldBe(future, TimeSpan.FromMilliseconds(1));
        reloaded.LastError.ShouldBe("boom");
    }
}
