using System;
using System.Collections.Generic;
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
/// Validates that a non-transactional unit of work buffers domain events at save time and flushes
/// them to the outbox only at its commit boundary.
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
    private sealed class OrderCreatedEvent(Guid orderId, int sequence) : IDistributedEvent
    {
        public Guid OrderId { get; } = orderId;
        public int Sequence { get; } = sequence;
    }

    private sealed class Order : AggregateRoot<Guid>
    {
        private Order() { }

        public Order(Guid id, string customer, int eventCount = 1) : base(id)
        {
            Customer = customer;
            for (var sequence = 1; sequence <= eventCount; sequence++)
            {
                AddDistributedEvent(new OrderCreatedEvent(id, sequence));
            }
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

    private sealed class FailSecondOutboxStageController
    {
        public bool Enabled { get; init; }
        public int Calls { get; set; }
    }

    private sealed class FailSecondOutboxStageEventBus(
        ITopicNameStrategy topicNameStrategy,
        IEventSerializer eventSerializer,
        IOutboxStore outboxStore,
        AetherEventBusOptions eventBusOptions,
        ICurrentSchema currentSchema,
        FailSecondOutboxStageController controller) : IDistributedEventBus
    {
        private readonly NoopEventBus _inner = new(
            topicNameStrategy, eventSerializer, outboxStore, eventBusOptions, currentSchema);

        public Task PublishAsync<TEvent>(TEvent payload, string? subject = null,
            CancellationToken cancellationToken = default) where TEvent : class =>
            _inner.PublishAsync(payload, subject, cancellationToken);

        public Task PublishAsync<TEvent>(TEvent payload, string? subject = null, bool useOutbox = true,
            CancellationToken cancellationToken = default) where TEvent : class =>
            _inner.PublishAsync(payload, subject, useOutbox, cancellationToken);

        public Task PublishAsync(IDistributedEvent @event, EventMetadata metadata, string? subject = null,
            bool useOutbox = true, CancellationToken cancellationToken = default)
        {
            controller.Calls++;
            if (controller.Enabled && controller.Calls == 2)
            {
                return Task.FromException(new InvalidOperationException("second outbox stage failed"));
            }

            return _inner.PublishAsync(@event, metadata, subject, useOutbox, cancellationToken);
        }

        public Task PublishEnvelopeAsync(byte[] serializedEnvelope, string topicName, string pubSubName,
            CancellationToken cancellationToken = default) =>
            _inner.PublishEnvelopeAsync(serializedEnvelope, topicName, pubSubName, cancellationToken);
    }

    private sealed class SimpleTopicNameStrategy : ITopicNameStrategy
    {
        public string GetTopicName(Type eventType)
        {
            var info = EventNameAttribute.GetEventNameInfo(eventType);
            return $"{info.EventName}.v{info.Version}";
        }
    }

    private IServiceProvider BuildProvider(bool failSecondOutboxStage = false)
    {
        var services = new ServiceCollection();

        services.AddAetherCore(_ => { });
        // Session search-path mode so a non-transactional UoW is usable (TransactionLocal, the
        // default, requires a transaction).
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.SessionSearchPath);
        services.AddAetherDomainEvents<TestDbContext>();
        services.AddAetherOutbox<TestDbContext>(options => options.Schema = _schema);

        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:orders" });
        services.AddSingleton<ITopicNameStrategy, SimpleTopicNameStrategy>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton(new FailSecondOutboxStageController { Enabled = failSecondOutboxStage });
        services.AddScoped<IDistributedEventBus, FailSecondOutboxStageEventBus>();

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

    [Fact]
    public async Task NonTransactional_SaveChanges_buffers_and_Commit_writes_outbox()
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
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order(Guid.NewGuid(), "Alice"));

            await uow.SaveChangesAsync();

            (await CountAsync("orders")).ShouldBe(1);
            (await CountAsync("OutboxMessages")).ShouldBe(0);

            await uow.CommitAsync();

            (await CountAsync("OutboxMessages")).ShouldBe(1);
        }
    }

    [Fact]
    public async Task NonTransactional_failed_second_stage_retries_without_duplicate_outbox_rows()
    {
        await using var sp = (ServiceProvider)BuildProvider(failSecondOutboxStage: true);
        await ArrangeSchemaAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order(Guid.NewGuid(), "Alice", eventCount: 2));
            await uow.SaveChangesAsync();

            await Should.ThrowAsync<InvalidOperationException>(() => uow.CommitAsync());
            uow.IsCompleted.ShouldBeFalse();
            (await CountAsync("OutboxMessages")).ShouldBe(0);

            await uow.CommitAsync();
            uow.IsCompleted.ShouldBeTrue();
        }

        (await CountAsync("OutboxMessages")).ShouldBe(2);
    }
}
