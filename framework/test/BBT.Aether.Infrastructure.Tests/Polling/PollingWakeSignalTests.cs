using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Polling;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.Polling;

public sealed class PollingWakeSignalTests
{
    private interface IMarker;

    [Fact]
    public async Task WaitAsync_ReturnsTrue_WhenSignaled()
    {
        var sut = new PollingWakeSignal<IMarker>();
        sut.Signal();
        (await sut.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_ReturnsFalse_OnTimeout()
    {
        var sut = new PollingWakeSignal<IMarker>();
        (await sut.WaitAsync(TimeSpan.FromMilliseconds(50))).ShouldBeFalse();
    }

    [Fact]
    public async Task Signal_IsCoalesced_NotAccumulated()
    {
        var sut = new PollingWakeSignal<IMarker>();
        sut.Signal();
        sut.Signal(); // must not throw, must not stack
        (await sut.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await sut.WaitAsync(TimeSpan.FromMilliseconds(50))).ShouldBeFalse();
    }

    [Fact]
    public async Task WaitAsync_Honors_Cancellation()
    {
        var sut = new PollingWakeSignal<IMarker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }
}
