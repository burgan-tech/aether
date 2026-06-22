using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// End-to-end validation of the domain-event / outbox pipeline through the multi-schema UnitOfWork.
/// Drives a REAL DI container (the framework's own registration extensions) with the default
/// <see cref="DomainEventDispatchStrategy.AlwaysUseOutbox"/> strategy so that an event raised by an
/// aggregate is written to the OUTBOX table inside the SAME shared transaction as the business data.
/// A real <see cref="EfCoreOutboxStore{TDbContext}"/> is wired (so the outbox row is genuinely
/// produced); only the message-broker leg of the event bus is stubbed (<see cref="NoopEventBus"/>),
/// because AlwaysUseOutbox never publishes to a broker during commit.
/// </summary>
[Collection("postgres")]
public sealed class OutboxWithinSharedTransactionTests(PostgresFixture fx)
{
    // GUID-suffixed schema avoids cross-test contention on the shared container. The name is already
    // lowercase hex + underscores, so DefaultSchemaNameFormatter.Format leaves it unchanged, meaning
    // the raw-created schema name matches the one ICurrentSchema resolves to.
    private readonly string _schema = "flow_orders_" + Guid.NewGuid().ToString("N");

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
            // Raising one distributed event on creation; written to the outbox at commit time.
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
                e.ToTable("orders"); // NO schema - resolved at runtime via SET LOCAL search_path
                e.HasKey(o => o.Id);
                e.Property(o => o.Customer).IsRequired();
            });
            modelBuilder.ConfigureOutbox();
        }
    }

    // Minimal event bus that runs the REAL outbox-store path (DistributedEventBusBase.StoreInOutboxAsync)
    // but no-ops the broker publish, so no Dapr client is required.
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

        // Core: IClock, IGuidGenerator, ICurrentSchema, ISchemaNameFormatter, etc.
        services.AddAetherCore(_ => { });

        // DbContext + UnitOfWork wiring (configurator, UoW manager, ambient accessor, provider).
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString);

        // Domain-event dispatcher (default strategy = AlwaysUseOutbox).
        services.AddAetherDomainEvents<TestDbContext>();

        // Outbox store -> EfCoreOutboxStore<TestDbContext> (real path that writes the outbox row).
        services.AddAetherOutbox<TestDbContext>();

        // Event bus dependencies (registered manually to avoid pulling in the Dapr bus).
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:orders" });
        services.AddSingleton<ITopicNameStrategy, SimpleTopicNameStrategy>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddScoped<IDistributedEventBus, NoopEventBus>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates the schema, then creates the `orders` and `OutboxMessages` tables using EF Core's own
    /// GenerateCreateScript() (so the DDL matches the entity shapes exactly). The script is executed
    /// against a setup connection whose search_path points at the test schema, so the unqualified
    /// CREATE TABLE statements land in the right schema.
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

        // Build a throwaway context purely to obtain the model -> create script.
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

    [Fact]
    public async Task Outbox_row_written_in_same_transaction_as_business_data()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            // Begin (synchronous) establishes the ambient UoW in this caller's flow, so both the
            // outbox store's IAetherDbContextProvider (resolved during commit) and our provider see
            // the active UoW without any manual ambient assignment.
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order(Guid.NewGuid(), "Alice"));

            await uow.CommitAsync();
        }

        (await CountAsync("orders")).ShouldBe(1);
        (await CountAsync("OutboxMessages")).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_discards_business_data_and_outbox()
    {
        var sp = BuildProvider();
        await ArrangeSchemaAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order(Guid.NewGuid(), "Bob"));

            // Flush business data into the transaction so a real rollback has something to discard.
            await uow.SaveChangesAsync();

            await uow.RollbackAsync();
        }

        (await CountAsync("orders")).ShouldBe(0);
        (await CountAsync("OutboxMessages")).ShouldBe(0);
    }
}
