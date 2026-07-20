using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;
using InboxMessage = BBT.Aether.Domain.Events.InboxMessage;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Events;

public sealed class FixedMessagingSchemaStoreTests
{
    public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : DbContext(options), IHasEfCoreOutbox, IHasEfCoreInbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
        public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureOutbox();
            modelBuilder.ConfigureInbox();
        }
    }

    [Fact]
    public async Task Outbox_store_resolves_context_from_configured_schema()
    {
        await using var context = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<MessagingDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                currentSchema.Name.ShouldBe("sys_queues");
                return context;
            });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var guids = Substitute.For<IGuidGenerator>();
        guids.Create().Returns(Guid.NewGuid());
        var sut = new EfCoreOutboxStore<MessagingDbContext>(
            provider,
            new SystemTextJsonEventSerializer(),
            guids,
            clock,
            new AetherOutboxOptions { Schema = "sys_queues" },
            currentSchema);

        await sut.StoreAsync(CreateEnvelope());

        await provider.Received(1).GetDbContextAsync(Arg.Any<CancellationToken>());
        currentSchema.Name.ShouldBe("tenant_a");
    }

    [Fact]
    public async Task Inbox_store_resolves_context_from_configured_schema()
    {
        await using var context = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<MessagingDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                currentSchema.Name.ShouldBe("sys_queues");
                return context;
            });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var sut = new EfCoreInboxStore<MessagingDbContext>(
            provider,
            new SystemTextJsonEventSerializer(),
            clock,
            new AetherInboxOptions { Schema = "sys_queues" },
            currentSchema);

        (await sut.HasProcessedAsync("missing")).ShouldBeFalse();

        await provider.Received(1).GetDbContextAsync(Arg.Any<CancellationToken>());
        currentSchema.Name.ShouldBe("tenant_a");
    }

    [Fact]
    public async Task Legacy_store_constructors_preserve_ambient_schema_behavior()
    {
        await using var context = CreateContext();
        var currentSchema = new StaticCurrentSchema("tenant_a");
        var provider = Substitute.For<IAetherDbContextProvider<MessagingDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                currentSchema.Name.ShouldBe("tenant_a");
                return context;
            });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var guids = Substitute.For<IGuidGenerator>();
        guids.Create().Returns(Guid.NewGuid());
        var outbox = new EfCoreOutboxStore<MessagingDbContext>(
            provider, new SystemTextJsonEventSerializer(), guids, clock);
        var inbox = new EfCoreInboxStore<MessagingDbContext>(
            provider,
            new SystemTextJsonEventSerializer(),
            clock,
            new AetherInboxOptions { Schema = "sys_queues" });

        await outbox.StoreAsync(CreateEnvelope());
        (await inbox.HasProcessedAsync("missing")).ShouldBeFalse();

        await provider.Received(2).GetDbContextAsync(Arg.Any<CancellationToken>());
        currentSchema.Name.ShouldBe("tenant_a");
    }

    private static MessagingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static CloudEventEnvelope CreateEnvelope() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = "TestEvent",
        Topic = "test-event",
        Data = new { Value = 42 }
    };
}
