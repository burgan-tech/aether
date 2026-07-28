using System;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using DomainOutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;
using DomainInboxMessage = BBT.Aether.Domain.Events.InboxMessage;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Regression coverage for the defect where <see cref="OutboxProcessor{TDbContext}.RunAsync"/> and
/// <see cref="InboxProcessor{TDbContext}.RunAsync"/> swallowed every exception internally and
/// returned 0, making a failed cycle indistinguishable from an empty poll to
/// <c>OutboxBackgroundService</c>/<c>InboxBackgroundService</c>. That made the background services'
/// own <c>catch</c> block — and the <c>MaxPollingInterval</c> back-off inside it — unreachable dead
/// code. These tests drive each processor against a service provider that has no registrations at
/// all, so the very first <c>GetRequiredService</c> call inside the processing method throws, and
/// assert the exception now reaches the caller instead of being logged and hidden behind a
/// misleading "0 processed" result.
/// </summary>
public sealed class ProcessorFailurePropagationTests
{
    private sealed class FakeOutboxDbContext(DbContextOptions<FakeOutboxDbContext> options)
        : DbContext(options), IHasEfCoreOutbox
    {
        public DbSet<DomainOutboxMessage> OutboxMessages => Set<DomainOutboxMessage>();
    }

    private sealed class FakeInboxDbContext(DbContextOptions<FakeInboxDbContext> options)
        : DbContext(options), IHasEfCoreInbox
    {
        public DbSet<DomainInboxMessage> InboxMessages => Set<DomainInboxMessage>();
    }

    private static IHostEnvironment FakeHostEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("processor-failure-tests");
        return env;
    }

    // A scope factory backed by an *empty* container: resolving any dependency other than the
    // container's own built-in services throws InvalidOperationException. This is what lets the
    // processing method fail deterministically, with no database and no timing involved.
    private static IServiceScopeFactory EmptyScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task OutboxProcessor_RunAsync_propagates_instead_of_swallowing()
    {
        var options = new AetherOutboxOptions { Schema = "propagation_test" };
        var processor = new OutboxProcessor<FakeOutboxDbContext>(
            EmptyScopeFactory(),
            new WorkerIdentity(FakeHostEnvironment()),
            new SystemClock(),
            NullLogger<OutboxProcessor<FakeOutboxDbContext>>.Instance,
            options);

        // Before the fix, RunAsync's own try/catch logged this and returned 0 — the caller
        // (OutboxBackgroundService) never saw a failure to back off from.
        await Should.ThrowAsync<InvalidOperationException>(() => processor.RunAsync());
    }

    [Fact]
    public async Task InboxProcessor_RunAsync_propagates_instead_of_swallowing()
    {
        var options = new AetherInboxOptions { Schema = "propagation_test" };
        var processor = new InboxProcessor<FakeInboxDbContext>(
            EmptyScopeFactory(),
            new WorkerIdentity(FakeHostEnvironment()),
            new SystemClock(),
            NullLogger<InboxProcessor<FakeInboxDbContext>>.Instance,
            options);

        // Before the fix, RunAsync's own try/catch logged this and returned 0 — the caller
        // (InboxBackgroundService) never saw a failure to back off from.
        await Should.ThrowAsync<InvalidOperationException>(() => processor.RunAsync());
    }
}
