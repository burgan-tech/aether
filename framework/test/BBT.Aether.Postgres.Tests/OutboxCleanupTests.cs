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
/// Characterisation tests for outbox retention cleanup. These pin the observable behaviour of
/// <see cref="OutboxProcessor{TDbContext}.CleanupProcessedMessagesAsync"/> — processed messages
/// past retention are deleted, messages still inside retention are kept — independent of whether
/// the implementation loads entities into the change tracker or issues a set-based DELETE.
/// </summary>
[Collection("postgres")]
public sealed class OutboxCleanupTests(PostgresFixture fx)
{
    private readonly string _schema = "cleanup_test_" + Guid.NewGuid().ToString("N");

    // Minimal IHostEnvironment stub — WorkerIdentity (resolved when the processor builds its
    // workerId) needs one, and NSubstitute is not a dependency of this test project.
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "outbox-cleanup-tests";
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

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.TransactionLocal);
        services.AddAetherOutbox<TestDbContext>(options =>
        {
            options.Schema = _schema;
            // Zero interval means CleanupSchedule.IsDue is always true, so every RunAsync
            // deterministically performs a cleanup pass instead of being gated by the hourly default.
            options.CleanupInterval = TimeSpan.Zero;
        });
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());

        // Event bus dependencies (registered manually to avoid pulling in the Dapr bus).
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:cleanup" });
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

    private async Task<long> CountRowsAsync()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM \"{_schema}\".\"OutboxMessages\"";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Cleanup_deletes_processed_messages_older_than_retention()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 2,
                    "ProcessedAt" = now() AT TIME ZONE 'utc' - interval '10 days'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var processor = sp.GetRequiredService<IOutboxProcessor>();
        await processor.RunAsync();

        (await CountRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_keeps_processed_messages_inside_retention()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 2,
                    "ProcessedAt" = now() AT TIME ZONE 'utc' - interval '1 hour'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var processor = sp.GetRequiredService<IOutboxProcessor>();
        await processor.RunAsync();

        (await CountRowsAsync()).ShouldBe(1);
    }
}
