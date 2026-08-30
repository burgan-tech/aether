using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Polling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.Polling;

public sealed class WakeAwarePollingTests
{
    [Fact]
    public async Task OutboxService_PollsImmediately_WhenSignaled()
    {
        var processed = new SemaphoreSlim(0);
        var processor = Substitute.For<IOutboxProcessor>();
        processor.RunAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { processed.Release(); return Task.FromResult(0); });

        var options = new AetherOutboxOptions
        {
            IdlePollingInterval = TimeSpan.FromSeconds(30),
            MaxPollingInterval = TimeSpan.FromSeconds(30),
            BusyPollingInterval = TimeSpan.FromMilliseconds(100)
        };
        var signal = new PollingWakeSignal<IOutboxProcessor>();
        var sut = new OutboxBackgroundService(
            processor, options, NullLogger<OutboxBackgroundService>.Instance, signal);

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        try
        {
            // Startup offset is also wake-aware: signal now, first run must happen fast.
            signal.Signal();
            (await processed.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
            // With a 30s idle interval, only a signal can trigger the next run this fast.
            signal.Signal();
            (await processed.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        }
        finally
        {
            cts.Cancel();
            await sut.StopAsync(CancellationToken.None);
        }
    }
}
