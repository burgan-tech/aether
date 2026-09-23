using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using InboxEntity = BBT.Aether.Domain.Events.InboxMessage;
using OutboxEntity = BBT.Aether.Domain.Events.OutboxMessage;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Pins how the inbox/outbox processors pace retention cleanup: at most once per
/// <c>CleanupInterval</c> per worker, except that a full batch keeps cleanup due so a backlog
/// drains one batch per cycle; a failure is deferred like a short batch (no per-cycle error spam),
/// and an outbox cleanup failure never discards the publish count RunAsync reports.
/// </summary>
[Collection(OutboxProcessorSpanCollection.Name)]
public sealed class CleanupScheduleTests
{
    private const int CleanupBatchSize = 10;

    public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : DbContext(options), IHasEfCoreOutbox, IHasEfCoreInbox
    {
        public DbSet<OutboxEntity> OutboxMessages => Set<OutboxEntity>();
        public DbSet<InboxEntity> InboxMessages => Set<InboxEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureOutbox();
            modelBuilder.ConfigureInbox();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CleanupBatchSize - 1)]
    public async Task Inbox_short_batch_defers_cleanup_until_the_interval_elapses(int deleted)
    {
        var cleanupStore = Substitute.For<IInboxCleanupStore>();
        cleanupStore.DeleteProcessedAsync(default, default, default).ReturnsForAnyArgs(deleted);
        var processor = CreateInboxProcessor(cleanupStore, TimeSpan.FromHours(1));

        await processor.RunAsync();
        await processor.RunAsync();

        await cleanupStore.ReceivedWithAnyArgs(1).DeleteProcessedAsync(default, default, default);
    }

    [Fact]
    public async Task Inbox_full_batch_keeps_cleanup_due_on_the_next_cycle()
    {
        var cleanupStore = Substitute.For<IInboxCleanupStore>();
        cleanupStore.DeleteProcessedAsync(default, default, default).ReturnsForAnyArgs(CleanupBatchSize, CleanupBatchSize, 3);
        var processor = CreateInboxProcessor(cleanupStore, TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
            await processor.RunAsync();

        await cleanupStore.ReceivedWithAnyArgs(3).DeleteProcessedAsync(default, default, default);
        await cleanupStore.Received().DeleteProcessedAsync(CleanupBatchSize, TimeSpan.FromDays(7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inbox_zero_interval_runs_cleanup_every_cycle()
    {
        var cleanupStore = Substitute.For<IInboxCleanupStore>();
        var processor = CreateInboxProcessor(cleanupStore, TimeSpan.Zero);

        await processor.RunAsync();
        await processor.RunAsync();

        await cleanupStore.ReceivedWithAnyArgs(2).DeleteProcessedAsync(default, default, default);
    }

    [Fact]
    public async Task Inbox_cleanup_failure_is_deferred_instead_of_retried_every_cycle()
    {
        var cleanupStore = Substitute.For<IInboxCleanupStore>();
        cleanupStore.DeleteProcessedAsync(default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down (test)"));
        var processor = CreateInboxProcessor(cleanupStore, TimeSpan.FromHours(1));

        await Should.NotThrowAsync(() => processor.RunAsync());
        await processor.RunAsync();

        await cleanupStore.ReceivedWithAnyArgs(1).DeleteProcessedAsync(default, default, default);
    }

    [Fact]
    public async Task Outbox_short_batch_defers_cleanup_and_full_batch_keeps_it_due()
    {
        var cleanupStore = Substitute.For<IOutboxCleanupStore>();
        cleanupStore.DeleteProcessedAsync(default, default, default).ReturnsForAnyArgs(CleanupBatchSize, 2);
        var processor = CreateOutboxProcessor(cleanupStore, TimeSpan.FromHours(1), leased: []);

        for (var i = 0; i < 4; i++)
            await processor.RunAsync();

        await cleanupStore.ReceivedWithAnyArgs(2).DeleteProcessedAsync(default, default, default);
        await cleanupStore.Received().DeleteProcessedAsync(CleanupBatchSize, TimeSpan.FromDays(7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Outbox_cleanup_failure_does_not_discard_the_publish_count()
    {
        var cleanupStore = Substitute.For<IOutboxCleanupStore>();
        cleanupStore.DeleteProcessedAsync(default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down (test)"));
        var processor = CreateOutboxProcessor(cleanupStore, TimeSpan.FromHours(1), leased: [MakeOutboxMessage()]);

        // Before: the cleanup exception escaped to RunAsync's catch-all, which returned 0 and made
        // the background service back off to the idle interval right after publishing a message.
        (await processor.RunAsync()).ShouldBe(1);
        await cleanupStore.ReceivedWithAnyArgs(1).DeleteProcessedAsync(default, default, default);
    }

    private static InboxProcessor<MessagingDbContext> CreateInboxProcessor(
        IInboxCleanupStore cleanupStore, TimeSpan cleanupInterval)
    {
        var leaseStore = Substitute.For<IInboxLeaseStore>();
        leaseStore.LeaseBatchAsync(default, default!, default, default)
            .ReturnsForAnyArgs(Array.Empty<InboxMessage>());

        var services = BaseServices();
        services.AddSingleton(leaseStore);
        services.AddSingleton(cleanupStore);
        var provider = services.BuildServiceProvider();

        return new InboxProcessor<MessagingDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WorkerIdentity(HostEnvironment()),
            NullLogger<InboxProcessor<MessagingDbContext>>.Instance,
            new AetherInboxOptions
            {
                Schema = "sys_queues",
                CleanupBatchSize = CleanupBatchSize,
                CleanupInterval = cleanupInterval,
            });
    }

    /// <summary>
    /// Publish always fails so phase 3 takes the read-only "not found" branch over an empty
    /// InMemory table (the success branch uses ExecuteUpdateAsync, which InMemory lacks).
    /// </summary>
    private static OutboxProcessor<MessagingDbContext> CreateOutboxProcessor(
        IOutboxCleanupStore cleanupStore, TimeSpan cleanupInterval, IReadOnlyList<OutboxMessage> leased)
    {
        var context = new MessagingDbContext(
            new DbContextOptionsBuilder<MessagingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var dbContextProvider = Substitute.For<IAetherDbContextProvider<MessagingDbContext>>();
        dbContextProvider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(context);

        var leaseStore = Substitute.For<IOutboxLeaseStore>();
        leaseStore.LeaseBatchAsync(default, default!, default, default).ReturnsForAnyArgs(leased);

        var eventBus = Substitute.For<IDistributedEventBus>();
        eventBus.PublishEnvelopeAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("publish failed (test)")));

        var services = BaseServices();
        services.AddSingleton(leaseStore);
        services.AddSingleton(cleanupStore);
        services.AddSingleton(eventBus);
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:vnext:test", PubSubName = "pubsub" });
        services.AddSingleton(dbContextProvider);
        var provider = services.BuildServiceProvider();

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        return new OutboxProcessor<MessagingDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WorkerIdentity(HostEnvironment()),
            clock,
            NullLogger<OutboxProcessor<MessagingDbContext>>.Instance,
            new AetherOutboxOptions
            {
                Schema = "sys_queues",
                BatchSize = 10,
                CleanupBatchSize = CleanupBatchSize,
                CleanupInterval = cleanupInterval,
            });
    }

    private static ServiceCollection BaseServices()
    {
        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(NullDisposable.Instance);

        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ => Substitute.For<IUnitOfWork>());

        var services = new ServiceCollection();
        services.AddSingleton(currentSchema);
        services.AddSingleton(uowManager);
        return services;
    }

    private static IHostEnvironment HostEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("cleanup-schedule-tests");
        return env;
    }

    private static OutboxMessage MakeOutboxMessage() => new()
    {
        Id = Guid.NewGuid(),
        EventName = "TestEvent",
        EventData = [],
        Status = OutboxMessageStatus.Pending,
        RetryCount = 0,
        ExtraProperties = new Dictionary<string, object>()
    };
}
