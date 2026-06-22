using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
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
/// Real-PostgreSQL validation of <see cref="JobDispatcher"/> as a DB state machine: atomic CAS claim
/// (Scheduled→Running), handler invocation, and framework-managed outcome recording (Completed / Retrying /
/// Failed for one-shot; back-to-Scheduled for recurring). A recording <see cref="FakeJobScheduler"/> asserts
/// that finished one-shot/failed jobs are deleted from the scheduler and recurring/retrying ones are not.
/// DI/schema setup mirrors <see cref="JobStoreCasTests"/> and <see cref="ArmingProcessorTests"/>.
/// </summary>
[Collection("postgres")]
public sealed class JobDispatcherTests(PostgresFixture fx)
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
    /// Test handler whose behavior (succeed / throw) is driven by a static switch, and which records its
    /// invocation count so tests can assert whether it ran at all (e.g. the CAS-skip path).
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
    /// Handler used by the "no held transaction" headline test. At the moment it runs it asserts there is
    /// no ambient UoW held by the dispatcher (<c>uowManager.Current is null</c>), then opens its OWN UoW,
    /// does a trivial DB write, and commits — proving handler-owned UoW works under the active schema scope.
    /// </summary>
    private sealed class UowAssertingHandler(IServiceProvider serviceProvider)
        : IBackgroundJobHandler<TestArgs>
    {
        public static bool? AmbientUowWasNull { get; private set; }
        public static bool HandlerOwnedUowCommitted { get; private set; }

        public static void Reset()
        {
            AmbientUowWasNull = null;
            HandlerOwnedUowCommitted = false;
        }

        public async Task HandleAsync(TestArgs args, CancellationToken cancellationToken)
        {
            var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();

            // The whole point: the dispatcher holds no open UoW/transaction across the handler.
            AmbientUowWasNull = uowManager.Current is null;

            // The handler owns its own UoW boundary; a trivial write proves it works under the schema scope.
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            var provider = serviceProvider.GetRequiredService<IAetherDbContextProvider<TestJobDbContext>>();
            var ctx = await provider.GetDbContextAsync(cancellationToken);
            await ctx.BackgroundJobs.AddAsync(
                new BackgroundJobInfo(Guid.NewGuid(), HandlerName, "handler-owned-" + Guid.NewGuid().ToString("N"))
                {
                    ExpressionValue = "@every 1m",
                    Payload = JsonDocument.Parse("{\"Value\":\"y\"}").RootElement.Clone(),
                    Status = BackgroundJobStatus.Pending,
                    Kind = JobKind.OneShot,
                }, cancellationToken);
            await uow.CommitAsync(cancellationToken);
            HandlerOwnedUowCommitted = true;
        }
    }

    /// <summary>
    /// Test invoker mirroring the framework's internal BackgroundJobInvoker&lt;TArgs&gt;: resolves the handler
    /// from the dispatch scope, deserializes the args, and invokes it. Implements the public
    /// <see cref="IBackgroundJobInvoker"/> so it can be placed directly into <c>options.Invokers</c>.
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

    /// <summary>Recording scheduler fake. Records every Delete call.</summary>
    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<(string handlerName, string jobName)> DeleteCalls { get; } = new();

        public Task ScheduleAsync(string handlerName, string jobName, string schedule,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicyOptions = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleOneShotAsync(string handlerName, string jobName, DateTime dueAtUtc,
            ReadOnlyMemory<byte> payload, JobScheduleFailurePolicy? failurePolicy = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string handlerName, string jobName, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add((handlerName, jobName));
            return Task.CompletedTask;
        }
    }

    private BackgroundJobOptions BuildOptions()
    {
        var options = new BackgroundJobOptions { Schema = _schema, MaxRetryCount = 3 };
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
        // The handler itself, resolved by BackgroundJobInvoker<TestArgs> from the dispatch scope.
        services.AddScoped<IBackgroundJobHandler<TestArgs>, TestHandler>();

        return services.BuildServiceProvider();
    }

    private JobDispatcher BuildDispatcher(IServiceProvider sp, BackgroundJobOptions options)
    {
        return new JobDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IEventSerializer>(),
            NullLogger<JobDispatcher>.Instance);
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

    private static string JobNameFor(Guid id) => "job-" + id.ToString("N");

    private static BackgroundJobInfo NewJob(Guid id, BackgroundJobStatus status, JobKind kind = JobKind.OneShot,
        int retryCount = 0, int maxRetry = 3)
    {
        return new BackgroundJobInfo(id, HandlerName, JobNameFor(id))
        {
            ExpressionValue = "@every 1m",
            Payload = JsonDocument.Parse("{\"Value\":\"x\"}").RootElement.Clone(),
            Status = status,
            Kind = kind,
            RetryCount = retryCount,
            MaxRetryCount = maxRetry,
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

    /// <summary>
    /// Builds the dispatch payload exactly the way BackgroundJobService does: wrap the args in a
    /// CloudEventEnvelope carrying the test schema, so the dispatcher's Change(...) scope is set and
    /// ExtractDataPayload yields the args.
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
    public async Task OneShot_success_completes_and_deletes()
    {
        TestHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        TestHandler.InvocationCount.ShouldBe(1);
        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Completed);
        reloaded.RunningSince.ShouldBeNull();
        scheduler.DeleteCalls.Count.ShouldBe(1);
        scheduler.DeleteCalls[0].jobName.ShouldBe(JobNameFor(id));
    }

    [Fact]
    public async Task Recurring_success_stays_scheduled()
    {
        TestHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled, JobKind.Recurring));

        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        TestHandler.InvocationCount.ShouldBe(1);
        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        reloaded.LastRunAt.ShouldNotBeNull();
        reloaded.RunningSince.ShouldBeNull();
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneShot_failure_with_retries_left_goes_retrying()
    {
        TestHandler.Reset();
        TestHandler.ShouldThrow = true;
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled, retryCount: 0, maxRetry: 3));

        var before = sp.GetRequiredService<IClock>().UtcNow;
        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Retrying);
        reloaded.RetryCount.ShouldBe(1);
        reloaded.LastError.ShouldNotBeNull();
        reloaded.RunningSince.ShouldBeNull();
        // retryCount=0 → backoff = base * 2^0 = RetryBaseDelay.
        var expected = before + options.RetryBaseDelay;
        reloaded.NextRetryAt!.Value.ShouldBe(expected, TimeSpan.FromSeconds(5));
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneShot_failure_exhausted_goes_failed()
    {
        TestHandler.Reset();
        TestHandler.ShouldThrow = true;
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled, retryCount: 3, maxRetry: 3));

        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Failed);
        reloaded.LastError.ShouldNotBeNull();
        reloaded.RunningSince.ShouldBeNull();
        scheduler.DeleteCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Recurring_failure_stays_scheduled()
    {
        TestHandler.Reset();
        TestHandler.ShouldThrow = true;
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled, JobKind.Recurring, retryCount: 0));

        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Scheduled);
        reloaded.LastError.ShouldNotBeNull();
        // Recurring jobs rely on the next cron occurrence and no longer increment RetryCount on failure
        // (the per-claim guarded TryReturnToScheduledAsync leaves RetryCount untouched).
        reloaded.RetryCount.ShouldBe(0);
        reloaded.RunningSince.ShouldBeNull();
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Already_running_is_skipped()
    {
        TestHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Running));

        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        // GetByJobNameAsync returns the Running row, but the atomic claim (TryClaimAsync pins Scheduled)
        // fails, so the handler never runs and status is unchanged.
        TestHandler.InvocationCount.ShouldBe(0);
        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Running);
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_job_is_noop()
    {
        TestHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();
        var sp = BuildProvider(scheduler, options);
        await ArrangeSchemaAsync(sp);

        // No row seeded for this job name → GetByJobNameAsync returns null; dispatcher logs and returns.
        await Should.NotThrowAsync(() =>
            BuildDispatcher(sp, options).DispatchAsync("job-does-not-exist", BuildPayload(sp)));

        TestHandler.InvocationCount.ShouldBe(0);
        scheduler.DeleteCalls.ShouldBeEmpty();
    }

    /// <summary>
    /// Headline test for H3: the handler runs with NO dispatcher-owned transaction. At the moment
    /// <c>HandleAsync</c> runs there is no ambient UoW; the handler then opens its OWN UoW, writes, and
    /// commits — proving handler-owned UoW works under the active schema scope.
    /// </summary>
    [Fact]
    public async Task Handler_runs_with_no_open_dispatcher_transaction()
    {
        TestHandler.Reset();
        UowAssertingHandler.Reset();
        var scheduler = new FakeJobScheduler();
        var options = BuildOptions();

        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestJobDbContext>(fx.ConnectionString);
        services.AddScoped<IJobStore, global::BBT.Aether.BackgroundJob.EfCoreJobStore<TestJobDbContext>>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IJobScheduler>(scheduler);
        services.AddSingleton(options);
        services.AddScoped<IBackgroundJobHandler<TestArgs>, UowAssertingHandler>();
        var sp = services.BuildServiceProvider();

        await ArrangeSchemaAsync(sp);

        var id = Guid.NewGuid();
        await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

        await BuildDispatcher(sp, options).DispatchAsync(JobNameFor(id), BuildPayload(sp));

        UowAssertingHandler.AmbientUowWasNull.ShouldBe(true);
        UowAssertingHandler.HandlerOwnedUowCommitted.ShouldBeTrue();

        var reloaded = await ReloadAsync(sp, id);
        reloaded!.Status.ShouldBe(BackgroundJobStatus.Completed);
        reloaded.RunningSince.ShouldBeNull();
    }
}
