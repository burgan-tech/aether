using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// End-to-end proof that a committed outbox write publishes a wake-up signal through the real
/// <see cref="CompositeUnitOfWork"/> commit path against a real PostgreSQL database, and that a
/// rollback publishes none. The unit tests over <see cref="OutboxSignalCollector"/> faked the
/// unit of work and so could not prove real commit semantics.
/// </summary>
[Collection("postgres")]
public sealed class OutboxSignalIntegrationTests(PostgresFixture fx)
{
    private readonly string _schema = "signal_test_" + Guid.NewGuid().ToString("N");

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

    private sealed class RecordingPublisher : IOutboxWakeupPublisher
    {
        public List<OutboxWakeupSignal> Published { get; } = [];

        public Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default)
        {
            Published.Add(signal);
            return Task.FromResult(true);
        }
    }

    private static CloudEventEnvelope Envelope(string subject) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = "TestEvent",
        Topic = "test-topic",
        Subject = subject,
        Data = System.Text.Encoding.UTF8.GetBytes("{}")
    };

    private IServiceProvider BuildProvider(RecordingPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.TransactionLocal);

        services.AddSingleton<IOutboxWakeupPublisher>(publisher);
        services.AddScoped<IOutboxSignalCollector, OutboxSignalCollector>();
        services.AddAetherOutbox<TestDbContext>(options =>
        {
            options.Schema = _schema;
            options.SignalEnabled = true;
        });

        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
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

        var configurator = sp.GetRequiredService<
            BBT.Aether.Uow.EntityFrameworkCore.IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new BBT.Aether.Uow.EntityFrameworkCore.SchemaScopeState()));
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

    [Fact]
    public async Task Committing_outbox_rows_publishes_one_signal_per_partition()
    {
        var publisher = new RecordingPublisher();
        var sp = BuildProvider(publisher);
        await SetupSchemaAsync(sp);

        await using (var scope = sp.CreateAsyncScope())
        {
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

                // Same Subject twice -> same partition -> must coalesce to one signal.
                await store.StoreAsync(Envelope(subject: "instance-a"));
                await store.StoreAsync(Envelope(subject: "instance-a"));

                publisher.Published.ShouldBeEmpty();   // nothing before commit
                await uow.CommitAsync();
            }
        }

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].Schema.ShouldBe(_schema);
    }

    [Fact]
    public async Task Rolling_back_publishes_no_signal()
    {
        var publisher = new RecordingPublisher();
        var sp = BuildProvider(publisher);
        await SetupSchemaAsync(sp);

        await using (var scope = sp.CreateAsyncScope())
        {
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                await store.StoreAsync(Envelope(subject: "instance-b"));
                await uow.RollbackAsync();
            }
        }

        publisher.Published.ShouldBeEmpty();
    }
}
