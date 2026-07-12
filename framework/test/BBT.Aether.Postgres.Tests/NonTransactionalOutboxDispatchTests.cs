using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
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
/// Validates <see cref="AetherDomainEventOptions.DispatchNonTransactionalEventsToOutbox"/>: when a
/// unit of work runs WITHOUT a shared transaction (IsTransactional=false — the per-step / autoSave
/// style used by long-running workflows), buffered domain events are normally dropped on commit
/// because there is no transaction to co-commit them. The flag opts such flows into flushing their
/// events to the outbox on commit (at-least-once, not atomic with the business writes).
/// <para>
/// Mirrors <see cref="OutboxWithinSharedTransactionTests"/>'s harness: a real
/// <see cref="DomainEventDispatchStrategy.AlwaysUseOutbox"/> pipeline with a real outbox store; only
/// the broker leg is stubbed.
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class NonTransactionalOutboxDispatchTests(PostgresFixture fx)
{
    private readonly string _schema = "flow_ntx_" + Guid.NewGuid().ToString("N");

    [EventName("OrderCreated", version: 1)]
    private sealed class OrderCreatedEvent(Guid orderId) : IDistributedEvent
    {
        public Guid OrderId { get; } = orderId;
    }

    private sealed class Order : AggregateRoot<Guid>
    {
        private Order() { }

        public Order(Guid id, string customer) : base(id)
        {
            Customer = customer;
            AddDistributedEvent(new OrderCreatedEvent(id));
        }

        public string Customer { get; private set; } = string.Empty;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreOutbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>(e =>
            {
                e.ToTable("orders");
                e.HasKey(o => o.Id);
                e.Property(o => o.Customer).IsRequired();
            });
            modelBuilder.ConfigureOutbox();
        }
    }

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

    private sealed class SimpleTopicNameStrategy : ITopicNameStrategy
    {
        public string GetTopicName(Type eventType)
        {
            var info = EventNameAttribute.GetEventNameInfo(eventType);
            return $"{info.EventName}.v{info.Version}";
        }
    }

    private IServiceProvider BuildProvider(bool dispatchNonTransactional)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        // Session search-path mode so a non-transactional UoW is usable (TransactionLocal, the
        // default, requires a transaction). This mirrors a deployment that runs non-transactional,
        // per-step/autoSave transitions — exactly the flows the new flag targets.
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.SessionSearchPath);
        services.AddAetherDomainEvents<TestDbContext>(o =>
            o.DispatchNonTransactionalEventsToOutbox = dispatchNonTransactional);
        services.AddAetherOutbox<TestDbContext>();

        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:orders" });
        services.AddSingleton<ITopicNameStrategy, SimpleTopicNameStrategy>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddScoped<IDistributedEventBus, NoopEventBus>();

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
            BBT.Aether.Uow.EntityFrameworkCore.IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
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

    private async Task<long> CountAsync(string table)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{_schema}\".\"{table}\"";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task RunNonTransactionalAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            // No transaction: the per-step / autoSave style. Business data is auto-committed by
            // SaveChanges; there is no shared transaction to co-commit events with.
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order(Guid.NewGuid(), "Alice"));

            // Persist business data (and let the sink buffer the aggregate's event).
            await uow.SaveChangesAsync();

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task Flag_On_NonTransactional_Commit_Writes_Event_To_Outbox()
    {
        var sp = BuildProvider(dispatchNonTransactional: true);
        await ArrangeSchemaAsync(sp);

        await RunNonTransactionalAsync(sp);

        (await CountAsync("orders")).ShouldBe(1);
        (await CountAsync("OutboxMessages")).ShouldBe(1);
    }

    [Fact]
    public async Task Flag_Off_NonTransactional_Commit_Drops_Event_Historical_Behavior()
    {
        var sp = BuildProvider(dispatchNonTransactional: false);
        await ArrangeSchemaAsync(sp);

        await RunNonTransactionalAsync(sp);

        // Business data is committed, but without a transaction and with the flag off the buffered
        // event is not dispatched — the historical behavior the flag preserves by default.
        (await CountAsync("orders")).ShouldBe(1);
        (await CountAsync("OutboxMessages")).ShouldBe(0);
    }
}
