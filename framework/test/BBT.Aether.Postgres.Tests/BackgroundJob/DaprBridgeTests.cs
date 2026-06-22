using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Dapr;
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
/// Real-PostgreSQL validation of <see cref="DaprJobExecutionBridge"/>. The headline assertion is that the
/// Dapr callback path no longer throws "No active UnitOfWork" when the bridge reads the provider-backed
/// <see cref="IJobStore.GetByJobNameAsync"/> — the bridge now wraps that lookup in its own UoW. A fake
/// <see cref="IJobDispatcher"/> records the (jobId, handlerName) it was dispatched with so the test isolates
/// the bridge from dispatch behavior. DI/schema/table/seed setup mirrors <see cref="JobDispatcherTests"/>.
/// </summary>
[Collection("postgres")]
public sealed class DaprBridgeTests(PostgresFixture fx)
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
    /// Recording dispatcher fake. Captures the (jobId, handlerName) it was dispatched with so the test can
    /// assert the bridge resolved the seeded job and delegated correctly, without invoking real dispatch logic.
    /// </summary>
    private sealed class FakeJobDispatcher : IJobDispatcher
    {
        public List<string> Dispatches { get; } = new();

        public Task DispatchAsync(string jobName, ReadOnlyMemory<byte> jobPayload,
            CancellationToken cancellationToken = default)
        {
            Dispatches.Add(jobName);
            return Task.CompletedTask;
        }
    }

    private IServiceProvider BuildProvider(FakeJobDispatcher dispatcher)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IJobDispatcher>(dispatcher);
        services.AddScoped<IJobExecutionBridge, DaprJobExecutionBridge>();

        return services.BuildServiceProvider();
    }

    private static IJobExecutionBridge BuildBridge(IServiceProvider sp)
    {
        // Resolve the bridge via DI (IJobExecutionBridge → DaprJobExecutionBridge) with the fake dispatcher.
        return sp.GetRequiredService<IJobExecutionBridge>();
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

    private static BackgroundJobInfo NewJob(Guid id, string jobName, BackgroundJobStatus status)
    {
        return new BackgroundJobInfo(id, HandlerName, jobName)
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{\"Value\":\"x\"}").RootElement.Clone(),
            Status = status,
            Kind = JobKind.OneShot,
            RetryCount = 0,
            MaxRetryCount = 3,
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

    /// <summary>
    /// Builds the Dapr callback payload exactly the way BackgroundJobService does: wrap the args in a
    /// CloudEventEnvelope carrying the test schema, serialized to bytes. The bridge extracts the schema to
    /// set its Change(...) scope before the provider-backed jobStore read.
    /// </summary>
    private byte[] BuildPayload(IServiceProvider sp)
    {
        var serializer = sp.GetRequiredService<IEventSerializer>();
        var envelope = new CloudEventEnvelope<TestArgs>
        {
            Type = HandlerName,
            Source = "urn:test",
            Data = new TestArgs { Value = "x" },
            Schema = _schema,
            DataContentType = "application/json",
        };
        return serializer.Serialize(envelope);
    }

    [Fact]
    public async Task ExecuteAsync_dispatches_job_without_throwing_no_active_uow()
    {
        var dispatcher = new FakeJobDispatcher();
        var sp = BuildProvider(dispatcher);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        var jobName = "job-" + id.ToString("N");
        // GetByJobNameAsync filters to active statuses (Scheduled || Running); seed Scheduled so it returns.
        await SeedAsync(sp, NewJob(id, jobName, BackgroundJobStatus.Scheduled));

        var bridge = BuildBridge(sp);

        // The whole point: this used to throw "No active UnitOfWork" from the provider-backed jobStore read.
        await Should.NotThrowAsync(() =>
            bridge.ExecuteAsync(jobName, BuildPayload(sp), CancellationToken.None));

        dispatcher.Dispatches.Count.ShouldBe(1);
        dispatcher.Dispatches[0].ShouldBe(jobName);
    }

    [Fact]
    public async Task ExecuteAsync_missing_job_is_noop()
    {
        var dispatcher = new FakeJobDispatcher();
        var sp = BuildProvider(dispatcher);
        await ArrangeSchemaAsync(sp);

        var bridge = BuildBridge(sp);

        // No row seeded for this job name → GetByJobNameAsync returns null; bridge logs and returns.
        await Should.NotThrowAsync(() =>
            bridge.ExecuteAsync("job-does-not-exist", BuildPayload(sp), CancellationToken.None));

        dispatcher.Dispatches.ShouldBeEmpty();
    }
}
