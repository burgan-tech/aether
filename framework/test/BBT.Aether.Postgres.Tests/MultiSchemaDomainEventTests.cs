using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
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
public sealed class MultiSchemaDomainEventTests(PostgresFixture fx)
{
    private readonly string _schemaA = "event_a_" + Guid.NewGuid().ToString("N");
    private readonly string _schemaB = "event_b_" + Guid.NewGuid().ToString("N");

    [EventName("OrderCreated", version: 1)]
    private sealed class OrderCreatedEvent(Guid orderId, int sequence) : IDistributedEvent
    {
        public Guid OrderId { get; } = orderId;
        public int Sequence { get; } = sequence;
    }

    private sealed class Order : AggregateRoot<Guid>
    {
        private Order() { }

        public Order(Guid id, string customer, int sequence = 1) : base(id)
        {
            Customer = customer;
            AddDistributedEvent(new OrderCreatedEvent(id, sequence));
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
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("orders");
                entity.HasKey(order => order.Id);
                entity.Property(order => order.Customer).IsRequired();
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
        protected override Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        protected override Task PublishToBrokerAsync(string topic, string pubSubName, byte[] serializedEnvelope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailDirectEventBus(
        ITopicNameStrategy topicNameStrategy,
        IEventSerializer eventSerializer,
        IOutboxStore outboxStore,
        AetherEventBusOptions eventBusOptions,
        ICurrentSchema currentSchema)
        : DistributedEventBusBase(topicNameStrategy, eventSerializer, outboxStore, eventBusOptions, currentSchema)
    {
        protected override Task PublishToBrokerAsync<TEvent>(string topic, byte[] serializedEnvelope,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("broker unavailable"));

        protected override Task PublishToBrokerAsync(string topic, string pubSubName, byte[] serializedEnvelope,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("broker unavailable"));
    }

    private sealed class StagingOrderRecorder
    {
        public List<(string Schema, int Sequence)> Calls { get; } = new();
    }

    private sealed class RecordingOutboxEventBus(
        ITopicNameStrategy topicNameStrategy,
        IEventSerializer eventSerializer,
        IOutboxStore outboxStore,
        AetherEventBusOptions eventBusOptions,
        ICurrentSchema currentSchema,
        StagingOrderRecorder recorder) : IDistributedEventBus
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
            recorder.Calls.Add((currentSchema.Name!, ((OrderCreatedEvent)@event).Sequence));
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

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.QualifiedNames);
        services.AddAetherDomainEvents<TestDbContext>();
        services.AddAetherOutbox<TestDbContext>();
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:orders" });
        services.AddSingleton<ITopicNameStrategy, SimpleTopicNameStrategy>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddSingleton<StagingOrderRecorder>();
        services.AddScoped<IDistributedEventBus, RecordingOutboxEventBus>();
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildScopedSchemaFallbackProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddScoped<ICurrentSchema, StaticCurrentSchema>();
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.QualifiedNames);
        services.AddAetherDomainEvents<TestDbContext>(options =>
            options.DispatchStrategy = DomainEventDispatchStrategy.PublishWithFallback);
        services.AddAetherOutbox<TestDbContext>();
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:test:orders" });
        services.AddSingleton<ITopicNameStrategy, SimpleTopicNameStrategy>();
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        services.AddScoped<IDistributedEventBus, FailDirectEventBus>();
        return services.BuildServiceProvider();
    }

    private async Task ArrangeSchemasAsync(IServiceProvider serviceProvider)
    {
        var configurator = serviceProvider.GetRequiredService<IAetherDbContextConfigurator<TestDbContext>>();

        await using var modelConnection = new NpgsqlConnection(fx.ConnectionString);
        await modelConnection.OpenAsync();
        await using var context = ActivatorUtilities.CreateInstance<TestDbContext>(
            serviceProvider,
            configurator.BuildOptions(modelConnection, _schemaA, new SchemaScopeState()));
        var template = context.Database.GenerateCreateScript();

        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();
        foreach (var schema in new[] { _schemaA, _schemaB })
        {
            await using var command = connection.CreateCommand();
            var schemaScript = template
                .Replace("__aether_schema__", schema, StringComparison.Ordinal)
                .Replace("\"public\".", $"\"{schema}\".", StringComparison.Ordinal);
            command.CommandText = schemaScript;
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> CountAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{table}\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<CloudEventEnvelope> ReadEnvelopeAsync(string schema, IEventSerializer serializer)
    {
        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"EventData\" FROM \"{schema}\".\"OutboxMessages\"";
        var payload = (byte[])(await command.ExecuteScalarAsync())!;
        return serializer.Deserialize<CloudEventEnvelope>(payload)!;
    }

    [Fact]
    public async Task NonTransactional_commit_writes_each_event_to_its_producing_schema()
    {
        await using var rootProvider = BuildProvider();
        await ArrangeSchemasAsync(rootProvider);

        await using var scope = rootProvider.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var currentSchema = serviceProvider.GetRequiredService<ICurrentSchema>();
        var manager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
        var dbContextProvider = serviceProvider.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();
        var serializer = serviceProvider.GetRequiredService<IEventSerializer>();

        using (currentSchema.Change(_schemaA))
        {
            await using var uow = manager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });

            var contextA = await dbContextProvider.GetDbContextAsync();
            contextA.Set<Order>().Add(new Order(Guid.NewGuid(), "Alice"));

            using (currentSchema.Change(_schemaB))
            {
                var contextB = await dbContextProvider.GetDbContextAsync();
                contextB.Set<Order>().Add(new Order(Guid.NewGuid(), "Bob"));
            }

            await uow.SaveChangesAsync();

            (await CountAsync(_schemaA, "OutboxMessages")).ShouldBe(0);
            (await CountAsync(_schemaB, "OutboxMessages")).ShouldBe(0);

            await uow.CommitAsync();
        }

        (await CountAsync(_schemaA, "OutboxMessages")).ShouldBe(1);
        (await CountAsync(_schemaB, "OutboxMessages")).ShouldBe(1);
        (await ReadEnvelopeAsync(_schemaA, serializer)).Schema.ShouldBe(_schemaA);
        (await ReadEnvelopeAsync(_schemaB, serializer)).Schema.ShouldBe(_schemaB);
    }

    [Fact]
    public async Task Publish_fallback_enters_producing_schema_on_new_scoped_current_schema()
    {
        await using var rootProvider = BuildScopedSchemaFallbackProvider();
        await ArrangeSchemasAsync(rootProvider);

        await using var scope = rootProvider.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var currentSchema = serviceProvider.GetRequiredService<ICurrentSchema>();
        var manager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
        var dbContextProvider = serviceProvider.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();
        var serializer = serviceProvider.GetRequiredService<IEventSerializer>();

        using (currentSchema.Change(_schemaA))
        {
            await using var uow = manager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            });

            var context = await dbContextProvider.GetDbContextAsync();
            context.Set<Order>().Add(new Order(Guid.NewGuid(), "Fallback"));
            await uow.CommitAsync();
        }

        (await CountAsync(_schemaA, "OutboxMessages")).ShouldBe(1);
        (await ReadEnvelopeAsync(_schemaA, serializer)).Schema.ShouldBe(_schemaA);
    }

    [Fact]
    public async Task AlwaysUseOutbox_stages_interleaved_schema_events_in_global_enqueue_order()
    {
        await using var rootProvider = BuildProvider();
        await ArrangeSchemasAsync(rootProvider);

        await using var scope = rootProvider.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var currentSchema = serviceProvider.GetRequiredService<ICurrentSchema>();
        var manager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
        var dbContextProvider = serviceProvider.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();
        var recorder = serviceProvider.GetRequiredService<StagingOrderRecorder>();

        using (currentSchema.Change(_schemaA))
        {
            await using var uow = manager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });

            var contextA = await dbContextProvider.GetDbContextAsync();
            contextA.Set<Order>().Add(new Order(Guid.NewGuid(), "A1", 1));
            await uow.SaveChangesAsync();

            using (currentSchema.Change(_schemaB))
            {
                var contextB = await dbContextProvider.GetDbContextAsync();
                contextB.Set<Order>().Add(new Order(Guid.NewGuid(), "B1", 2));
                await uow.SaveChangesAsync();
            }

            contextA.Set<Order>().Add(new Order(Guid.NewGuid(), "A2", 3));
            await uow.SaveChangesAsync();
            await uow.CommitAsync();
        }

        recorder.Calls.ShouldBe(new[]
        {
            (_schemaA, 1),
            (_schemaB, 2),
            (_schemaA, 3)
        });
        (await CountAsync(_schemaA, "OutboxMessages")).ShouldBe(2);
        (await CountAsync(_schemaB, "OutboxMessages")).ShouldBe(1);
    }
}
