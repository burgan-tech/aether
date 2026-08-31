using System;
using System.Diagnostics;
using System.Linq;
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
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Events;

/// <summary>
/// Pins EfCoreOutboxStore.StoreAsync's trace-identity persistence: the drop's ambient trace
/// context (TraceParent/TraceState) is copied onto the stored row's ExtraProperties the same way
/// TopicName already is, and the ambient activity gains an outbox.message_id tag. Together these
/// are what let OutboxProcessor's Outbox.Process span re-join the originating trace without ever
/// deserializing the envelope.
/// </summary>
public sealed class EfCoreOutboxStoreTraceTests
{
    private const string TestSourceName = "Test.EfCoreOutboxStoreTrace";

    public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : DbContext(options), IHasEfCoreOutbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureOutbox();
        }
    }

    [Fact]
    public async Task StoreAsync_persists_ambient_trace_context_and_tags_the_ambient_activity()
    {
        using var listener = CreateListener();
        using var source = new ActivitySource(TestSourceName);
        using var ambient = source.StartActivity("EventBus.Publish", ActivityKind.Producer);
        ambient.ShouldNotBeNull();
        ambient!.TraceStateString = "congo=t61rcWkgMzE";

        var sut = CreateSut(out var context);

        await sut.StoreAsync(CreateEnvelope());

        var stored = context.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity).Single();

        stored.ExtraProperties["TraceParent"].ShouldBe(ambient.Id);
        stored.ExtraProperties["TraceState"].ShouldBe("congo=t61rcWkgMzE");
        ambient.GetTagItem("outbox.message_id").ShouldBe(stored.Id.ToString());
    }

    [Fact]
    public async Task StoreAsync_omits_trace_state_when_the_ambient_activity_has_none()
    {
        using var listener = CreateListener();
        using var source = new ActivitySource(TestSourceName);
        using var ambient = source.StartActivity("EventBus.Publish", ActivityKind.Producer);
        ambient.ShouldNotBeNull();

        var sut = CreateSut(out var context);

        await sut.StoreAsync(CreateEnvelope());

        var stored = context.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity).Single();

        stored.ExtraProperties["TraceParent"].ShouldBe(ambient!.Id);
        stored.ExtraProperties.ShouldNotContainKey("TraceState");
    }

    [Fact]
    public async Task StoreAsync_writes_no_trace_keys_and_does_not_throw_when_nothing_is_ambient()
    {
        var sut = CreateSut(out var context);

        await Should.NotThrowAsync(async () => await sut.StoreAsync(CreateEnvelope()));

        var stored = context.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity).Single();

        stored.ExtraProperties.ShouldNotContainKey("TraceParent");
        stored.ExtraProperties.ShouldNotContainKey("TraceState");
    }

    private static ActivityListener CreateListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static EfCoreOutboxStore<MessagingDbContext> CreateSut(out MessagingDbContext context)
    {
        context = new MessagingDbContext(
            new DbContextOptionsBuilder<MessagingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);

        var provider = Substitute.For<IAetherDbContextProvider<MessagingDbContext>>();
        provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(context);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var guids = Substitute.For<IGuidGenerator>();
        guids.Create().Returns(_ => Guid.NewGuid());

        return new EfCoreOutboxStore<MessagingDbContext>(
            provider,
            new SystemTextJsonEventSerializer(),
            guids,
            clock,
            new AetherOutboxOptions { Schema = "sys_queues" },
            new StaticCurrentSchema("sys_queues"));
    }

    private static CloudEventEnvelope CreateEnvelope() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = "TestEvent",
        Topic = "test-event",
        Data = new { Value = 42 }
    };
}
