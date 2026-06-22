using System;
using System.Collections.Generic;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests.BackgroundJob;

/// <summary>
/// Real-PostgreSQL validation of <see cref="DaprJobExecutionBridge"/>. The bridge does no database work itself:
/// it extracts the CloudEventEnvelope, sets the schema scope, and dispatches by job name. A fake
/// <see cref="IJobDispatcher"/> records the dispatched job name (and the ambient schema at dispatch time) so the
/// test isolates the bridge from dispatch behavior. DI/schema/table setup mirrors <see cref="JobDispatcherTests"/>.
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
    /// Recording dispatcher fake. Captures the job name it was dispatched with — and the ambient schema in effect
    /// at dispatch time — so the test can assert the bridge delegated by name under the correct schema scope,
    /// without invoking real dispatch logic. <see cref="ICurrentSchema"/> is injected so the fake can read the
    /// ambient schema the bridge set via <c>Change(...)</c> before calling the dispatcher.
    /// </summary>
    private sealed class FakeJobDispatcher(ICurrentSchema currentSchema) : IJobDispatcher
    {
        public List<string> Dispatches { get; } = new();
        public List<string?> SchemasAtDispatch { get; } = new();

        public Task DispatchAsync(string jobName, ReadOnlyMemory<byte> jobPayload,
            CancellationToken cancellationToken = default)
        {
            Dispatches.Add(jobName);
            SchemasAtDispatch.Add(currentSchema.Name);
            return Task.CompletedTask;
        }
    }

    private IServiceProvider BuildProvider(out FakeJobDispatcher dispatcher)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        // The fake reads the ambient schema via ICurrentSchema (AsyncLocal-backed, so any instance observes the
        // same stack the bridge pushed). Registered as a singleton instance the test holds for assertions.
        var fake = new FakeJobDispatcher(new CurrentSchema(new DefaultSchemaNameFormatter()));
        services.AddSingleton<IJobDispatcher>(fake);
        services.AddScoped<IJobExecutionBridge, DaprJobExecutionBridge>();

        var sp = services.BuildServiceProvider();
        dispatcher = fake;
        return sp;
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
    public async Task ExecuteAsync_dispatches_by_job_name()
    {
        var sp = BuildProvider(out var dispatcher);
        await ArrangeSchemaAsync(sp);

        var jobName = "job-" + Guid.NewGuid().ToString("N");

        var bridge = BuildBridge(sp);

        // The bridge does no DB work; it extracts the envelope, sets the schema scope, and dispatches by name.
        await Should.NotThrowAsync(() =>
            bridge.ExecuteAsync(jobName, BuildPayload(sp), CancellationToken.None));

        dispatcher.Dispatches.Count.ShouldBe(1);
        dispatcher.Dispatches[0].ShouldBe(jobName);
    }

    [Fact]
    public async Task ExecuteAsync_sets_schema_scope()
    {
        var sp = BuildProvider(out var dispatcher);
        await ArrangeSchemaAsync(sp);

        var jobName = "job-" + Guid.NewGuid().ToString("N");

        var bridge = BuildBridge(sp);

        await Should.NotThrowAsync(() =>
            bridge.ExecuteAsync(jobName, BuildPayload(sp), CancellationToken.None));

        // The fake captured the ambient schema (AsyncLocal) at dispatch time; it must equal the envelope schema.
        dispatcher.SchemasAtDispatch.Count.ShouldBe(1);
        dispatcher.SchemasAtDispatch[0].ShouldBe(_schema);
    }

    [Fact]
    public async Task ExecuteAsync_dispatches_unknown_job_name_to_dispatcher()
    {
        var sp = BuildProvider(out var dispatcher);
        await ArrangeSchemaAsync(sp);

        var bridge = BuildBridge(sp);

        // No row exists for this job name. The bridge no longer reads the DB — it simply hands the name to the
        // dispatcher (which is responsible for the load/claim and the no-op-on-missing behavior).
        await Should.NotThrowAsync(() =>
            bridge.ExecuteAsync("job-does-not-exist", BuildPayload(sp), CancellationToken.None));

        dispatcher.Dispatches.Count.ShouldBe(1);
        dispatcher.Dispatches[0].ShouldBe("job-does-not-exist");
    }
}
