using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

/// <summary>
/// Real-PostgreSQL validation of the arming-lease methods added to <see cref="IJobStore"/>:
/// <see cref="IJobStore.TryTransitionFromArmingAsync"/> and
/// <see cref="IJobStore.ResetExpiredArmingClaimsAsync"/>.
/// </summary>
[Collection("postgres")]
public sealed class JobStoreArmingLeaseTests(PostgresFixture fx)
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
            .Replace("__aether_schema__", _schema, StringComparison.Ordinal);

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

    private static BackgroundJobInfo NewJob(Guid id, BackgroundJobStatus status,
        Guid? armingToken = null, DateTime? armingUntil = null, JobKind kind = JobKind.OneShot)
    {
        return new BackgroundJobInfo(id, "TestHandler", "job-" + id.ToString("N"))
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
            Status = status,
            Kind = kind,
            MaxRetryCount = 3,
            ArmingToken = armingToken,
            ArmingUntil = armingUntil,
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
    public async Task TryTransitionFromArming_succeeds_and_clears_token()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var armingUntil = DateTime.UtcNow.AddSeconds(30);
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending, armingToken: token, armingUntil: armingUntil));

        var before = DateTime.UtcNow;
        bool result;
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
                result = await store.TryTransitionFromArmingAsync(
                    id, token, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled);
                await uow.CommitAsync();
            }
        }
        var after = DateTime.UtcNow;

        result.ShouldBeTrue();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        reloaded.ArmingToken.ShouldBeNull();
        reloaded.ArmingUntil.ShouldBeNull();
        reloaded.ModifiedAt.ShouldNotBeNull();
        reloaded.ModifiedAt!.Value.ShouldBeGreaterThanOrEqualTo(before);
        reloaded.ModifiedAt.Value.ShouldBeLessThanOrEqualTo(after.AddSeconds(2));
    }

    [Fact]
    public async Task TryTransitionFromArming_wrong_token_returns_false()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var wrongToken = Guid.NewGuid();
        var armingUntil = DateTime.UtcNow.AddSeconds(30);
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending, armingToken: token, armingUntil: armingUntil));

        bool result;
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
                result = await store.TryTransitionFromArmingAsync(
                    id, wrongToken, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled);
                await uow.CommitAsync();
            }
        }

        result.ShouldBeFalse();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Pending);
        reloaded.ArmingToken.ShouldBe(token);
    }

    [Fact]
    public async Task TryTransitionFromArming_abort_reverts_to_original_status()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var armingUntil = DateTime.UtcNow.AddSeconds(30);
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Retrying, armingToken: token, armingUntil: armingUntil));

        bool result;
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
                result = await store.TryTransitionFromArmingAsync(
                    id, token, BackgroundJobStatus.Retrying, BackgroundJobStatus.Retrying);
                await uow.CommitAsync();
            }
        }

        result.ShouldBeTrue();

        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Retrying);
        reloaded.ArmingToken.ShouldBeNull();
        reloaded.ArmingUntil.ShouldBeNull();
    }

    [Fact]
    public async Task ResetExpiredArmingClaims_resets_expired_rows_to_pending()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var now = DateTime.UtcNow;
        var expiredId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var expiredToken = Guid.NewGuid();
        var freshToken = Guid.NewGuid();

        await SeedAsync(sp,
            NewJob(expiredId, BackgroundJobStatus.Pending,
                armingToken: expiredToken, armingUntil: now.AddMinutes(-5)),
            NewJob(freshId, BackgroundJobStatus.Pending,
                armingToken: freshToken, armingUntil: now.AddMinutes(5)));

        int count;
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
                count = await store.ResetExpiredArmingClaimsAsync(now, batchSize: 100);
                await uow.CommitAsync();
            }
        }

        count.ShouldBe(1);

        var reloadedExpired = await ReloadAsync(sp, expiredId);
        reloadedExpired.ShouldNotBeNull();
        reloadedExpired!.Status.ShouldBe(BackgroundJobStatus.Pending);
        reloadedExpired.ArmingToken.ShouldBeNull();
        reloadedExpired.ArmingUntil.ShouldBeNull();

        var reloadedFresh = await ReloadAsync(sp, freshId);
        reloadedFresh.ShouldNotBeNull();
        reloadedFresh!.ArmingToken.ShouldBe(freshToken);
    }

    private IServiceProvider BuildProviderWithLeaseStore()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        // Register EfCore fallback explicitly (Npgsql override added in Task 10)
        services.AddScoped<IJobArmingLeaseStore,
            global::BBT.Aether.BackgroundJob.EfCoreJobArmingLeaseStore<TestJobDbContext>>();
        return services.BuildServiceProvider();
    }

    private IServiceProvider BuildProviderWithGatedFallbackLeaseStore(
        FallbackCandidateReadGateInterceptor interceptor)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(
            fx.ConnectionString,
            configure: (_, builder) => builder.AddInterceptors(interceptor));
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddScoped<IJobArmingLeaseStore,
            global::BBT.Aether.BackgroundJob.EfCoreJobArmingLeaseStore<TestJobDbContext>>();
        return services.BuildServiceProvider();
    }

    private sealed class FallbackCandidateReadGateInterceptor : DbCommandInterceptor
    {
        private int _candidateReadIntercepted;

        public bool Enabled { get; set; }

        public TaskCompletionSource CandidateRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueClaim { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("ArmingToken", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _candidateReadIntercepted, 1, 0) == 0)
            {
                CandidateRead.TrySetResult();
                await ContinueClaim.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task EfCoreLeaseStore_does_not_claim_job_cancelled_after_candidate_read()
    {
        var interceptor = new FallbackCandidateReadGateInterceptor();
        var sp = BuildProviderWithGatedFallbackLeaseStore(interceptor);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending));
        interceptor.Enabled = true;

        var claimTask = ClaimBatchConcurrently(sp, "fallback-worker", batchSize: 1);
        await interceptor.CandidateRead.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var cancelScope = sp.CreateAsyncScope())
        {
            var services = cancelScope.ServiceProvider;
            using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                (await services.GetRequiredService<IJobStore>()
                    .TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeTrue();
                await uow.CommitAsync();
            }
        }

        interceptor.ContinueClaim.TrySetResult();
        var claims = await claimTask;

        claims.ShouldBeEmpty();
        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
        reloaded.ArmingToken.ShouldBeNull();
        reloaded.ArmingUntil.ShouldBeNull();
    }

    [Fact]
    public async Task EfCoreLeaseStore_claims_due_jobs_and_sets_token()
    {
        var sp = BuildProviderWithLeaseStore();
        await ArrangeSchemaAsync(sp);

        var pendingId = Guid.NewGuid();
        var futureId = Guid.NewGuid();

        var pendingJob = NewJob(pendingId, BackgroundJobStatus.Pending);
        pendingJob.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);

        var futureJob = NewJob(futureId, BackgroundJobStatus.Retrying);
        futureJob.NextRetryAt = DateTime.UtcNow.AddMinutes(10);

        await SeedAsync(sp, pendingJob);
        await SeedAsync(sp, futureJob);

        IReadOnlyList<BackgroundJobArmingClaim> claims;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            using (ssp.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = ssp.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                claims = await ssp.GetRequiredService<IJobArmingLeaseStore>()
                    .ClaimBatchAsync(100, "worker-1", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }
        }

        claims.Count.ShouldBe(1);
        claims[0].Job.Id.ShouldBe(pendingId);
        claims[0].OriginalStatus.ShouldBe(BackgroundJobStatus.Pending);
        claims[0].ArmingToken.ShouldNotBe(Guid.Empty);

        var reloaded = await ReloadAsync(sp, pendingId);
        reloaded!.ArmingToken.ShouldNotBeNull();
        reloaded.ArmingUntil.ShouldNotBeNull();
        reloaded.ArmingUntil!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [Fact]
    public async Task TryTransitionFromArming_does_not_revive_cancelled_row_with_same_token()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        await SeedAsync(sp, NewJob(
            id,
            BackgroundJobStatus.Pending,
            armingToken: token,
            armingUntil: DateTime.UtcNow.AddSeconds(30)));

        await using (var cancelScope = sp.CreateAsyncScope())
        {
            var services = cancelScope.ServiceProvider;
            using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                await services.GetRequiredService<IJobStore>().UpdateStatusAsync(
                    id, BackgroundJobStatus.Cancelled, DateTime.UtcNow);
                await uow.CommitAsync();
            }
        }

        bool transitioned;
        await using (var confirmScope = sp.CreateAsyncScope())
        {
            var services = confirmScope.ServiceProvider;
            using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                transitioned = await services.GetRequiredService<IJobStore>()
                    .TryTransitionFromArmingAsync(
                        id, token, BackgroundJobStatus.Pending, BackgroundJobStatus.Scheduled);
                await uow.CommitAsync();
            }
        }

        transitioned.ShouldBeFalse();
        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
    }

    [Fact]
    public async Task Cancellation_between_lease_claim_and_confirmation_leaves_job_cancelled()
    {
        var sp = BuildProviderWithNpgsqlLeaseStore();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Pending));

        var claim = (await ClaimBatchConcurrently(sp, "claiming-worker", batchSize: 1)).Single();

        await using (var cancelScope = sp.CreateAsyncScope())
        {
            var services = cancelScope.ServiceProvider;
            using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                (await services.GetRequiredService<IJobStore>()
                    .TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeTrue();
                await uow.CommitAsync();
            }
        }

        await using (var confirmScope = sp.CreateAsyncScope())
        {
            var services = confirmScope.ServiceProvider;
            using (services.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                (await services.GetRequiredService<IJobStore>().TryTransitionFromArmingAsync(
                    id,
                    claim.ArmingToken,
                    claim.OriginalStatus,
                    BackgroundJobStatus.Scheduled)).ShouldBeFalse();
                await uow.CommitAsync();
            }
        }

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
        reloaded.ArmingToken.ShouldBeNull();
    }

    private IServiceProvider BuildProviderWithNpgsqlLeaseStore()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        // Explicitly register NpgsqlJobArmingLeaseStore to test the Npgsql implementation directly
        services.AddScoped<IJobArmingLeaseStore,
            global::BBT.Aether.BackgroundJob.NpgsqlJobArmingLeaseStore<TestJobDbContext>>();
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildProviderWithFixedSchemaNpgsqlLeaseStore()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(
            fx.ConnectionString,
            SchemaSwitchingMode.QualifiedNames);
        services.AddAetherBackgroundJob<TestJobDbContext>(options => options.Schema = _schema);
        return services.BuildServiceProvider();
    }

    private async Task<List<BackgroundJobArmingClaim>> ClaimBatchConcurrently(
        IServiceProvider sp, string workerId, int batchSize)
    {
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        using (ssp.GetRequiredService<ICurrentSchema>().Change(_schema))
        {
            await using var uow = ssp.GetRequiredService<IUnitOfWorkManager>().Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            var claims = (await ssp.GetRequiredService<IJobArmingLeaseStore>()
                .ClaimBatchAsync(batchSize, workerId, TimeSpan.FromSeconds(30))).ToList();
            await uow.CommitAsync();
            return claims;
        }
    }

    [Fact]
    public async Task NpgsqlLeaseStore_two_concurrent_pods_claim_disjoint_batches()
    {
        var sp = BuildProviderWithNpgsqlLeaseStore();
        await ArrangeSchemaAsync(sp);

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
        {
            var job = NewJob(id, BackgroundJobStatus.Pending);
            job.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);
            await SeedAsync(sp, job);
        }

        // Simulate two pods racing to claim from the pool of 4 jobs
        var podATask = ClaimBatchConcurrently(sp, "pod-a", batchSize: 2);
        var podBTask = ClaimBatchConcurrently(sp, "pod-b", batchSize: 2);
        var claimsA = await podATask;
        var claimsB = await podBTask;

        // Together they must claim all 4 jobs with no overlap
        var allClaimed = claimsA.Concat(claimsB).Select(c => c.Job.Id).ToList();
        allClaimed.Count.ShouldBe(4);
        allClaimed.Distinct().Count().ShouldBe(4, "no job claimed by both pods");
    }

    [Fact]
    public async Task NpgsqlLeaseStore_qualified_names_uses_configured_schema_not_caller_schema()
    {
        const string tenantSchema = "tenant_a";
        using var sp = BuildProviderWithFixedSchemaNpgsqlLeaseStore();
        await ArrangeSchemaAsync(sp);
        await using (var connection = new NpgsqlConnection(fx.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{tenantSchema}\";";
            await command.ExecuteNonQueryAsync();
        }

        var id = Guid.NewGuid();
        var job = NewJob(id, BackgroundJobStatus.Pending);
        job.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);
        await SeedAsync(sp, job);

        IReadOnlyList<BackgroundJobArmingClaim> claims;
        await using (var scope = sp.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var currentSchema = services.GetRequiredService<ICurrentSchema>();
            using (currentSchema.Change(tenantSchema))
            {
                await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions
                    {
                        Scope = UnitOfWorkScopeOption.RequiresNew,
                        IsTransactional = true
                    });
                var leaseStore = services.GetRequiredService<IJobArmingLeaseStore>();
                leaseStore.ShouldBeOfType<
                    global::BBT.Aether.BackgroundJob.NpgsqlJobArmingLeaseStore<TestJobDbContext>>();
                claims = await leaseStore.ClaimBatchAsync(
                    10, "worker", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();

                currentSchema.Name.ShouldBe(tenantSchema);
            }
        }

        claims.Count.ShouldBe(1);
        claims[0].Job.Id.ShouldBe(id);
        (await RelationExistsAsync(tenantSchema, "BackgroundJobs")).ShouldBeFalse();
    }

    private async Task<bool> RelationExistsAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@relation) IS NOT NULL";
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ResetExpiredArmingClaims_preserves_retrying_status()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var expiredToken = Guid.NewGuid();

        // Seed a Retrying job with an expired arming claim
        var job = NewJob(id, BackgroundJobStatus.Retrying,
            armingToken: expiredToken, armingUntil: DateTime.UtcNow.AddSeconds(-5));
        job.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);

        await SeedAsync(sp, job);

        // Act: reset expired claims
        int reset;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ssp = scope.ServiceProvider;
            using (ssp.GetRequiredService<ICurrentSchema>().Change(_schema))
            {
                await using var uow = ssp.GetRequiredService<IUnitOfWorkManager>().Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                reset = await ssp.GetRequiredService<IJobStore>()
                    .ResetExpiredArmingClaimsAsync(DateTime.UtcNow, 100);
                await uow.CommitAsync();
            }
        }

        reset.ShouldBe(1);

        // Assert: Status stays Retrying, arming fields cleared
        var reloaded = await ReloadAsync(sp, id);
        reloaded.ShouldNotBeNull();
        reloaded!.ArmingToken.ShouldBeNull();
        reloaded.ArmingUntil.ShouldBeNull();
        reloaded.Status.ShouldBe(BackgroundJobStatus.Retrying, "expired claim must not change status");
        reloaded.NextRetryAt.ShouldNotBeNull("NextRetryAt must be preserved");
    }

    [Fact]
    public async Task GetDueForArming_excludes_actively_claimed_rows()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        var claimedId = Guid.NewGuid();
        var freeId = Guid.NewGuid();

        // This job has a live arming claim (should be excluded)
        var claimed = NewJob(claimedId, BackgroundJobStatus.Pending,
            armingToken: Guid.NewGuid(), armingUntil: DateTime.UtcNow.AddSeconds(30));
        claimed.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);

        // This job is free (should be returned)
        var free = NewJob(freeId, BackgroundJobStatus.Pending);
        free.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);

        await SeedAsync(sp, claimed, free);

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        using (ssp.GetRequiredService<ICurrentSchema>().Change(_schema))
        {
            await using var uow = ssp.GetRequiredService<IUnitOfWorkManager>().Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            var due = await ssp.GetRequiredService<IJobStore>()
                .GetDueForArmingAsync(DateTime.UtcNow, 100);
            await uow.CommitAsync();

            due.Count.ShouldBe(1);
            due[0].Id.ShouldBe(freeId);
        }
    }
}
