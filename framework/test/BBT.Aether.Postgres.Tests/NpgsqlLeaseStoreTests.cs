using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Aether.Clock;
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

[Collection("postgres")]
public sealed class NpgsqlLeaseStoreTests(PostgresFixture fx)
{
    private readonly string _schema = "lease_test_" + Guid.NewGuid().ToString("N");

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

    private IServiceProvider BuildProvider(
        SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal)
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, mode);
        services.AddAetherOutbox<TestDbContext>();
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

    [Fact]
    public async Task LeaseBatch_returns_pending_messages_and_locks_them()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].Status.ShouldBe(OutboxMessageStatus.Processing);
            leased[0].LockedBy.ShouldBe("worker-1");
            leased[0].LockedUntil.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task LeaseBatch_skips_already_locked_messages()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            // Worker 1 leases the message
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(60));
                await uow.CommitAsync();
            }

            // Worker 2 should get nothing (message already locked)
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> worker2Batch;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                worker2Batch = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(60));
                await uow.CommitAsync();
            }

            worker2Batch.Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task LeaseBatch_does_not_pick_up_dead_letter_messages()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // Mark as dead letter via direct SQL
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE \"{_schema}\".\"OutboxMessages\" SET \"Status\" = 3";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(0);
        }
    }

    [Theory]
    [InlineData(SchemaSwitchingMode.TransactionLocal)]
    [InlineData(SchemaSwitchingMode.SessionSearchPath)]
    [InlineData(SchemaSwitchingMode.QualifiedNames)]
    public async Task LeaseBatch_uses_qualified_relation_without_transaction(
        SchemaSwitchingMode mode)
    {
        var sp = BuildProvider(mode);
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        await using (uowManager.Begin(new UnitOfWorkOptions
                     {
                         Scope = UnitOfWorkScopeOption.RequiresNew,
                         IsTransactional = false
                     }))
        {
            var leased = await leaseStore.LeaseBatchAsync(
                10, "non-transactional-worker", TimeSpan.FromSeconds(30));

            leased.Count.ShouldBe(1);
            leased[0].LockedBy.ShouldBe("non-transactional-worker");
        }
    }
}
