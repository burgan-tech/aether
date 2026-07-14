using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
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
    private sealed class OrderCreatedEvent : IDistributedEvent;

    private sealed class Order : AggregateRoot<Guid>
    {
        private Order() { }

        public Order(Guid id) : base(id)
        {
            AddDistributedEvent(new OrderCreatedEvent());
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().HasKey(order => order.Id);
        }
    }

    private sealed class InMemoryConfigurator : IAetherDbContextConfigurator<TestDbContext>
    {
        public DbConnection CreateConnection() => new StubDbConnection();

        public DbContextOptions<TestDbContext> BuildOptions(
            DbConnection sharedConnection,
            string schema,
            SchemaScopeState state) => new DbContextOptionsBuilder<TestDbContext>()
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
}
