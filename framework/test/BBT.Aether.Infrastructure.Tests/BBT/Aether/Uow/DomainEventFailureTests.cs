using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class DomainEventFailureTests
{
    [EventName("OrderCreated", version: 1)]
    private sealed class OrderCreatedEvent(int sequence) : IDistributedEvent
    {
        public int Sequence { get; } = sequence;
    }

    private sealed class Order : AggregateRoot<Guid>
    {
        private Order() { }

        public Order(Guid id, int sequence = 1) : base(id)
        {
            AddDistributedEvent(new OrderCreatedEvent(sequence));
        }
    }

    private sealed class TestDbContext(
        DbContextOptions<TestDbContext> options,
        ICurrentSchema currentSchema)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreOutbox
    {
        public static Func<string?, bool>? FailSave { get; set; }
        public DbSet<Domain.Events.OutboxMessage> OutboxMessages => Set<Domain.Events.OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().HasKey(order => order.Id);
            modelBuilder.ConfigureOutbox();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            FailSave?.Invoke(currentSchema.Name) == true
                ? Task.FromException<int>(new InvalidOperationException($"save failed for {currentSchema.Name}"))
                : base.SaveChangesAsync(cancellationToken);
    }

    private sealed class InMemoryConfigurator : IAetherDbContextConfigurator<TestDbContext>
    {
        public DbConnection CreateConnection() => new StubDbConnection();

        public DbContextOptions<TestDbContext> BuildOptions(
            DbConnection sharedConnection,
            string schema,
            SchemaScopeState state) => BuildInMemoryOptions();

        public DbContextOptions<TestDbContext> BuildOwnedOptions(string schema) => BuildInMemoryOptions();

        private static DbContextOptions<TestDbContext> BuildInMemoryOptions() =>
            new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase($"domain-events-{Guid.NewGuid():N}")
                .Options;
    }

    private sealed class StubDbConnection : DbConnection
    {
        private ConnectionState _state;

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "domain-events";
        public override string DataSource => "in-memory";
        public override string ServerVersion => "1";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAetherDbContextConfigurator<TestDbContext>, InMemoryConfigurator>();
        services.AddSingleton<ICurrentSchema>(new StaticCurrentSchema());
        return services.BuildServiceProvider();
    }

    private static async Task BufferOneEventAsync(CompositeUnitOfWork uow)
    {
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = false });
        var context = await uow.GetDbContextAsync<TestDbContext>("schema_a");
        context.Set<Order>().Add(new Order(Guid.NewGuid()));
        await uow.SaveChangesAsync();
    }

    [Fact]
    public async Task Commit_with_pending_events_and_no_dispatcher_throws_and_stays_incomplete()
    {
        await using var provider = BuildProvider();
        await using var uow = new CompositeUnitOfWork(provider);
        await BufferOneEventAsync(uow);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => uow.CommitAsync());

        exception.Message.ShouldContain("IDomainEventDispatcher");
        uow.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispatcher_failure_propagates_original_exception_and_stays_incomplete()
    {
        await using var provider = BuildProvider();
        var eventBus = Substitute.For<IDistributedEventBus>();
        var expected = new InvalidOperationException("outbox unavailable");
        eventBus.PublishAsync(
                Arg.Any<IDistributedEvent>(),
                Arg.Any<EventMetadata>(),
                Arg.Any<string?>(),
                true,
                Arg.Any<CancellationToken>())
            .ThrowsAsync(expected);

        var dispatcher = new DomainEventDispatcher(
            eventBus,
            new AetherDomainEventOptions(),
            NullLogger<DomainEventDispatcher>.Instance,
            provider);
        await using var uow = new CompositeUnitOfWork(provider, dispatcher, new AetherDomainEventOptions());
        await BufferOneEventAsync(uow);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => uow.CommitAsync());

        exception.ShouldBeSameAs(expected);
        uow.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Publish_fallback_preserves_interleaved_schema_order_and_retries_from_failed_run()
    {
        await using var provider = BuildProvider();
        var currentSchema = provider.GetRequiredService<ICurrentSchema>();
        var dispatcher = Substitute.For<IDomainEventDispatcher>();
        var observed = new System.Collections.Generic.List<(string Schema, int Sequence)>();
        var failSchemaB = true;

        dispatcher.PublishDirectlyAsync(
                Arg.Any<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var events = callInfo.ArgAt<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(0);
                observed.AddRange(events.Select(envelope =>
                    (currentSchema.Name!, ((OrderCreatedEvent)envelope.Event).Sequence)));
                return failSchemaB && currentSchema.Name == "schema_b"
                    ? Task.FromException(new InvalidOperationException("broker unavailable"))
                    : Task.CompletedTask;
            });
        dispatcher.WriteToOutboxInNewScopeAsync(
                Arg.Any<string>(),
                Arg.Any<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("outbox unavailable")));

        await using var uow = new CompositeUnitOfWork(
            provider,
            dispatcher,
            new AetherDomainEventOptions { DispatchStrategy = DomainEventDispatchStrategy.PublishWithFallback });
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = false });

        var contextA = await uow.GetDbContextAsync<TestDbContext>("schema_a");
        contextA.Set<Order>().Add(new Order(Guid.NewGuid(), 1));
        await uow.SaveChangesAsync();

        var contextB = await uow.GetDbContextAsync<TestDbContext>("schema_b");
        contextB.Set<Order>().Add(new Order(Guid.NewGuid(), 2));
        await uow.SaveChangesAsync();

        contextA.Set<Order>().Add(new Order(Guid.NewGuid(), 3));
        await uow.SaveChangesAsync();

        await Should.ThrowAsync<AggregateException>(() => uow.CommitAsync());

        observed.ShouldBe(new[] { ("schema_a", 1), ("schema_b", 2) });
        uow.IsCompleted.ShouldBeFalse();

        failSchemaB = false;
        await uow.CommitAsync();

        observed.ShouldBe(new[]
        {
            ("schema_a", 1),
            ("schema_b", 2),
            ("schema_b", 2),
            ("schema_a", 3)
        });
        uow.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Nontransactional_outbox_retry_resumes_after_each_durable_schema_run()
    {
        await using var provider = BuildProvider();
        var currentSchema = provider.GetRequiredService<ICurrentSchema>();
        var dispatcher = Substitute.For<IDomainEventDispatcher>();
        var dispatches = new System.Collections.Generic.List<(string Schema, int Sequence)>();
        var contexts = new System.Collections.Generic.Dictionary<string, TestDbContext>();
        var failBOnce = true;

        dispatcher.DispatchEventsAsync(
                Arg.Any<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var schema = currentSchema.Name!;
                foreach (var envelope in call.ArgAt<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(0))
                {
                    var sequence = ((OrderCreatedEvent)envelope.Event).Sequence;
                    dispatches.Add((schema, sequence));
                    contexts[schema].OutboxMessages.Add(new Domain.Events.OutboxMessage(
                        Guid.NewGuid(), $"event-{sequence}", []));
                }
                return Task.CompletedTask;
            });

        await using var uow = new CompositeUnitOfWork(provider, dispatcher, new AetherDomainEventOptions());
        await uow.InitializeAsync(new UnitOfWorkOptions { IsTransactional = false });
        var contextA = await uow.GetDbContextAsync<TestDbContext>("schema_a");
        var contextB = await uow.GetDbContextAsync<TestDbContext>("schema_b");
        contexts.Add("schema_a", contextA);
        contexts.Add("schema_b", contextB);

        contextA.Set<Order>().Add(new Order(Guid.NewGuid(), 1));
        await uow.SaveChangesAsync();
        contextB.Set<Order>().Add(new Order(Guid.NewGuid(), 2));
        await uow.SaveChangesAsync();
        contextA.Set<Order>().Add(new Order(Guid.NewGuid(), 3));
        await uow.SaveChangesAsync();

        TestDbContext.FailSave = schema => schema == "schema_b" && failBOnce && !(failBOnce = false);
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => uow.CommitAsync());
            await uow.CommitAsync();
        }
        finally
        {
            TestDbContext.FailSave = null;
        }

        dispatches.ShouldBe(new[]
        {
            ("schema_a", 1), ("schema_b", 2), ("schema_b", 2), ("schema_a", 3)
        });
        contextA.OutboxMessages.Count().ShouldBe(2);
        contextB.OutboxMessages.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Transactional_direct_publish_retry_does_not_commit_physical_transaction_twice()
    {
        await using var provider = BuildProvider();
        var dispatcher = Substitute.For<IDomainEventDispatcher>();
        var publishAttempts = 0;
        dispatcher.PublishDirectlyAsync(
                Arg.Any<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ++publishAttempts == 1
                ? Task.FromException(new InvalidOperationException("publish unavailable"))
                : Task.CompletedTask);
        dispatcher.WriteToOutboxInNewScopeAsync(
                Arg.Any<string>(),
                Arg.Any<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("outbox unavailable")));

        await using var uow = new CompositeUnitOfWork(provider, dispatcher,
            new AetherDomainEventOptions { DispatchStrategy = DomainEventDispatchStrategy.PublishWithFallback });
        await BufferOneEventAsync(uow);
        var transaction = new CountingDbTransaction(new StubDbConnection());
        typeof(CompositeUnitOfWork).GetField("_transaction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(uow, transaction);

        await Should.ThrowAsync<AggregateException>(() => uow.CommitAsync());
        await uow.CommitAsync();

        transaction.CommitCalls.ShouldBe(1);
        publishAttempts.ShouldBe(2);
        uow.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Successful_commit_retry_invokes_only_completed_handlers()
    {
        await using var provider = BuildProvider();
        var dispatcher = Substitute.For<IDomainEventDispatcher>();
        var attempts = 0;
        dispatcher.DispatchEventsAsync(
                Arg.Any<System.Collections.Generic.IEnumerable<DomainEventEnvelope>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("temporary"))
                : Task.CompletedTask);
        await using var uow = new CompositeUnitOfWork(provider, dispatcher, new AetherDomainEventOptions());
        await BufferOneEventAsync(uow);
        var completed = 0;
        var failed = 0;
        uow.OnCompleted(_ => { completed++; return Task.CompletedTask; });
        uow.OnFailed((_, _) => { failed++; return Task.CompletedTask; });

        await Should.ThrowAsync<InvalidOperationException>(() => uow.CommitAsync());
        await uow.CommitAsync();
        await uow.DisposeAsync();

        completed.ShouldBe(1);
        failed.ShouldBe(0);
    }

    private sealed class CountingDbTransaction(DbConnection connection) : DbTransaction
    {
        public int CommitCalls { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => CommitCalls++;
        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return Task.CompletedTask;
        }
        public override void Rollback() { }
    }
}
