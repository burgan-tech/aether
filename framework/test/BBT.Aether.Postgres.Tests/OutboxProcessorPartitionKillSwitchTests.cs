using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// Proves <see cref="AetherOutboxOptions.PartitionedLeasingEnabled"/> is enforced in exactly one
/// place — <see cref="OutboxProcessor{TDbContext}"/>'s lease call — and nowhere else. A caller can
/// supply a partition filter to <see cref="IOutboxProcessor.RunAsync"/> at any time; whether that
/// filter actually narrows the lease query depends solely on the flag.
/// </summary>
[Collection("postgres")]
public sealed class OutboxProcessorPartitionKillSwitchTests(PostgresFixture fx)
{
    private readonly string _schema = "partition_switch_test_" + Guid.NewGuid().ToString("N");

    // Minimal IHostEnvironment stub — WorkerIdentity (resolved when the processor builds its
    // workerId) needs one, and NSubstitute is not a dependency of this test project.
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "outbox-partition-switch-tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreOutbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureOutbox();
        }
    }

    // Minimal event bus that runs the real outbox-store path but no-ops the broker publish, so no
    // Dapr client is required. ProcessOutboxMessagesAsync resolves IDistributedEventBus unconditionally
    // even when there is nothing pending to lease, so it must be registered for RunAsync to succeed.
    private sealed class NoopEventBus(
        ITopicNameStrategy topicNameStrategy,
        IEventSerializer eventSerializer,
        IOutboxStore outboxStore,
        AetherEventBusOptions eventBusOptions,
        ICurrentSchema currentSchema)
        : DistributedEventBusBase(topicNameStrategy, eventSerializer, outboxStore, eventBusOptions, currentSchema)
    {
        protected override Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        protected override Task PublishToBrokerAsync(string topic, string pubSubName, byte[] serializedEnvelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Topic strategy with no IHostEnvironment dependency.
    private sealed class SimpleTopicNameStrategy : ITopicNameStrategy
    {
        public string GetTopicName(Type eventType)
        {
            var info = EventNameAttribute.GetEventNameInfo(eventType);
            return $"{info.EventName}.v{info.Version}";
        }
    }

    private IServiceProvider BuildProvider(bool partitionedLeasingEnabled)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.TransactionLocal);
        services.AddAetherOutbox<TestDbContext>(options =>
        {
            options.Schema = _schema;
            options.PartitionedLeasingEnabled = partitionedLeasingEnabled;
        });
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());

        // Event bus dependencies (registered manually to avoid pulling in the Dapr bus).
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:partition-switch" });
        services.AddSingleton<ITopicNameStrategy, SimpleTopicNameStrategy>();
        services.AddScoped<IDistributedEventBus, NoopEventBus>();

        return services.BuildServiceProvider();
    }

    private async Task SetupSchemaAsync(IServiceProvider sp)
    {
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA \"{_schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var configurator = sp.GetRequiredService<IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript()
            .Replace(AetherSchemaModel.QuotedPlaceholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace(AetherSchemaModel.Placeholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace(
                $"CREATE SCHEMA \"{_schema}\";",
                $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\";",
                StringComparison.Ordinal);

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

    private async Task InsertPendingMessageAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            await outboxStore.StoreAsync(new CloudEventEnvelope
            {
                Id = Guid.NewGuid().ToString(),
                Type = "TestEvent",
                Topic = "test-topic",
                Data = System.Text.Encoding.UTF8.GetBytes("{}")
            });

            await uow.CommitAsync();
        }
    }

    private async Task SetPartitionAsync(short partitionId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE \"{_schema}\".\"OutboxMessages\" SET \"PartitionId\" = {partitionId}";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> GetStatusAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"Status\" FROM \"{_schema}\".\"OutboxMessages\"";
        var status = await cmd.ExecuteScalarAsync();
        return status?.ToString() ?? throw new InvalidOperationException("No row found.");
    }

    [Fact]
    public async Task Flag_off_ignores_the_supplied_filter_and_leases_the_row_anyway()
    {
        var sp = BuildProvider(partitionedLeasingEnabled: false);
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);
        await SetPartitionAsync(7);

        var processor = sp.GetRequiredService<IOutboxProcessor>();
        // Filter names partition 3; the row lives in partition 7. With the flag off this must
        // make no difference — the query stays unfiltered.
        await processor.RunAsync(new short[] { 3 });

        // Processed == leased-and-published here; there is nothing else pending, so a status of
        // Processed proves the row was leased despite the mismatched filter.
        (await GetStatusAsync()).ShouldBe(((int)OutboxMessageStatus.Processed).ToString());
    }

    [Fact]
    public async Task Flag_on_honours_the_supplied_filter_and_leaves_the_row_untouched()
    {
        var sp = BuildProvider(partitionedLeasingEnabled: true);
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);
        await SetPartitionAsync(7);

        var processor = sp.GetRequiredService<IOutboxProcessor>();
        // Filter names partition 3; the row lives in partition 7. With the flag on the mismatch
        // must keep the row un-leased.
        await processor.RunAsync(new short[] { 3 });

        (await GetStatusAsync()).ShouldBe(((int)OutboxMessageStatus.Pending).ToString());
    }
}
