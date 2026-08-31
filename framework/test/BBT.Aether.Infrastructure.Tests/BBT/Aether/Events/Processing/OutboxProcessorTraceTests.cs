using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using OutboxEntity = BBT.Aether.Domain.Events.OutboxMessage;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Pins the shape of OutboxProcessor's per-message "Outbox.Process" span under the episode-
/// separation policy (trace_refactor): the publish is its own trace ROOT — never re-parented onto
/// the originating transition or the worker loop. Both the origin (when the row's TraceParent is
/// present and parseable) and the worker-loop ambient (when one is active) are attached only as
/// causation links, so an origin span's own parent/child duration semantics are never stretched
/// across the worker hop.
/// </summary>
public sealed class OutboxProcessorTraceTests
{
    private const string OriginSourceName = "Test.Origin";
    private const string LoopSourceName = "Test.WorkerLoop";

    public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : DbContext(options), IHasEfCoreOutbox
    {
        public DbSet<OutboxEntity> OutboxMessages => Set<OutboxEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureOutbox();
        }
    }

    [Fact]
    public async Task Message_with_stored_trace_parent_is_a_new_root_that_links_the_origin_and_the_worker_loop()
    {
        using var listener = CreateListener(out var started);

        using var originSource = new ActivitySource(OriginSourceName);
        using var origin = originSource.StartActivity("EventBus.Publish", ActivityKind.Producer);
        origin.ShouldNotBeNull();
        var traceParent = origin!.Id!;
        var traceState = "vendor=origin-state";
        origin.TraceStateString = traceState;
        origin.Stop(); // the drop's publish span has already ended by the time the processor runs

        var message = MakeMessage(traceParent: traceParent, traceState: traceState);

        using var loopSource = new ActivitySource(LoopSourceName);
        using var loop = loopSource.StartActivity("Outbox.Poll", ActivityKind.Internal);
        loop.ShouldNotBeNull();

        await RunProcessorAsync(new[] { message });

        var activity = started.ShouldHaveSingleItem();
        activity.OperationName.ShouldBe("Outbox.Process");

        // New root: never a child of the origin or the worker loop.
        activity.ParentSpanId.ShouldBe(default(ActivitySpanId));
        activity.TraceId.ShouldNotBe(origin.TraceId);
        activity.TraceId.ShouldNotBe(loop!.TraceId);

        // Both are attached as links (causation), never as the parent.
        activity.Links.ShouldContain(l => l.Context.TraceId == origin.TraceId && l.Context.SpanId == origin.SpanId
            && l.Context.TraceState == traceState);
        activity.Links.ShouldContain(l => l.Context.SpanId == loop.SpanId);
        activity.Links.Count().ShouldBe(2);

        activity.GetTagItem("event.name").ShouldBe("TestEvent");
        activity.GetTagItem("outbox.message_id").ShouldBe(message.Id.ToString());
        activity.GetTagItem("outbox.retry_count").ShouldBe(0);
    }

    [Fact]
    public async Task Message_without_trace_parent_is_still_a_new_root_and_links_only_the_worker_loop()
    {
        using var listener = CreateListener(out var started);

        var message = MakeMessage(traceParent: null);

        using var loopSource = new ActivitySource(LoopSourceName);
        using var loop = loopSource.StartActivity("Outbox.Poll", ActivityKind.Internal);
        loop.ShouldNotBeNull();

        await Should.NotThrowAsync(async () => await RunProcessorAsync(new[] { message }));

        var activity = started.ShouldHaveSingleItem();
        activity.OperationName.ShouldBe("Outbox.Process");
        activity.ParentSpanId.ShouldBe(default(ActivitySpanId));
        activity.TraceId.ShouldNotBe(loop!.TraceId);
        activity.Links.ShouldHaveSingleItem();
        activity.Links.ShouldContain(l => l.Context.SpanId == loop.SpanId);
        activity.GetTagItem("event.name").ShouldBe("TestEvent");
        activity.GetTagItem("outbox.message_id").ShouldBe(message.Id.ToString());
        activity.GetTagItem("outbox.retry_count").ShouldBe(0);
    }

    [Fact]
    public async Task Message_with_garbage_trace_parent_drops_the_origin_link_but_stays_a_root()
    {
        using var listener = CreateListener(out var started);

        var message = MakeMessage(traceParent: "not-a-real-traceparent");

        using var loopSource = new ActivitySource(LoopSourceName);
        using var loop = loopSource.StartActivity("Outbox.Poll", ActivityKind.Internal);
        loop.ShouldNotBeNull();

        await Should.NotThrowAsync(async () => await RunProcessorAsync(new[] { message }));

        var activity = started.ShouldHaveSingleItem();
        activity.ParentSpanId.ShouldBe(default(ActivitySpanId));
        activity.TraceId.ShouldNotBe(loop!.TraceId);
        activity.Links.ShouldHaveSingleItem();
        activity.Links.ShouldContain(l => l.Context.SpanId == loop.SpanId);
    }

    [Fact]
    public async Task Message_with_trace_parent_and_no_ambient_loop_is_a_root_that_links_only_the_origin()
    {
        using var listener = CreateListener(out var started);

        using var originSource = new ActivitySource(OriginSourceName);
        using var origin = originSource.StartActivity("EventBus.Publish", ActivityKind.Producer);
        origin.ShouldNotBeNull();
        var traceParent = origin!.Id!;
        origin.Stop();

        var message = MakeMessage(traceParent: traceParent);

        // No ambient worker-loop activity active here — Activity.Current is null.
        await RunProcessorAsync(new[] { message });

        var activity = started.ShouldHaveSingleItem();
        activity.ParentSpanId.ShouldBe(default(ActivitySpanId));
        activity.TraceId.ShouldNotBe(origin.TraceId);
        activity.Links.ShouldHaveSingleItem();
        activity.Links.ShouldContain(l => l.Context.TraceId == origin.TraceId && l.Context.SpanId == origin.SpanId);
    }

    private static OutboxMessage MakeMessage(string? traceParent, string? traceState = null)
    {
        var extraProperties = new Dictionary<string, object>();
        if (traceParent != null)
            extraProperties["TraceParent"] = traceParent;
        if (traceState != null)
            extraProperties["TraceState"] = traceState;

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventName = "TestEvent",
            EventData = [],
            Status = OutboxMessageStatus.Pending,
            RetryCount = 0,
            ExtraProperties = extraProperties
        };
    }

    private static ActivityListener CreateListener(out List<Activity> started)
    {
        var list = new List<Activity>();
        started = list;
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == InfrastructureActivitySource.SourceName
                || s.Name == OriginSourceName
                || s.Name == LoopSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "Outbox.Process") list.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Drives OutboxProcessor.RunAsync through a minimal real DI container (so its own
    /// scopeFactory.CreateAsyncScope() call resolves) wired with fakes for every collaborator.
    /// The publish call always fails: that routes phase 3 through the read-only
    /// "lease expired/not found" branch (FirstOrDefaultAsync over an empty InMemory table returns
    /// null and the loop just continues) instead of ExecuteUpdateAsync, which the EF Core InMemory
    /// provider does not support — irrelevant to what this test asserts, which is only the shape
    /// of the per-message activity started in phase 2.
    /// </summary>
    private static async Task RunProcessorAsync(IReadOnlyList<OutboxMessage> messages)
    {
        var context = new MessagingDbContext(
            new DbContextOptionsBuilder<MessagingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);

        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(NullDisposable.Instance);

        var uow = Substitute.For<IUnitOfWork>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);

        var leaseStore = Substitute.For<IOutboxLeaseStore>();
        leaseStore.LeaseBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        var eventBus = Substitute.For<IDistributedEventBus>();
        eventBus.PublishEnvelopeAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("publish failed (test)")));

        var dbContextProvider = Substitute.For<IAetherDbContextProvider<MessagingDbContext>>();
        dbContextProvider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(context);

        var services = new ServiceCollection();
        services.AddSingleton(currentSchema);
        services.AddSingleton(uowManager);
        services.AddSingleton(eventBus);
        services.AddSingleton(new AetherEventBusOptions { DefaultSource = "urn:vnext:test", PubSubName = "pubsub" });
        services.AddSingleton(leaseStore);
        services.AddSingleton(dbContextProvider);
        await using var provider = services.BuildServiceProvider();

        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("outbox-processor-trace-tests");

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        var processor = new OutboxProcessor<MessagingDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WorkerIdentity(env),
            clock,
            NullLogger<OutboxProcessor<MessagingDbContext>>.Instance,
            new AetherOutboxOptions { Schema = "sys_queues", BatchSize = 10 });

        await processor.RunAsync();
    }
}
