using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

#pragma warning disable CS0618

namespace BBT.Aether.DistributedLock.Dapr;

public class DaprDistributedLockHandleTests
{
    private const string StoreName = "lockstore";
    private const string LockKey = "test:resource";
    private const string Owner = "machine:abc123";
    private readonly DaprClient _daprClient = Substitute.For<DaprClient>();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    private DaprDistributedLockHandle CreateHandle() =>
        new(_daprClient, StoreName, LockKey, Owner, _logger);

    [Fact]
    public void Properties_ReturnsCorrectValues()
    {
        var handle = CreateHandle();

        handle.LockKey.ShouldBe(LockKey);
        handle.Owner.ShouldBe(Owner);
    }

    [Fact]
    public async Task ExtendAsync_NotSupported_AlwaysReturnsFalse()
    {
        var handle = CreateHandle();
        var result = await handle.ExtendAsync(120);

        result.ShouldBeFalse();
        await _daprClient.DidNotReceive().Lock(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseAsync_CallsUnlock()
    {
        var handle = CreateHandle();
        await handle.ReleaseAsync();

        await _daprClient.Received(1).Unlock(StoreName, LockKey, Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseAsync_DoubleRelease_OnlyUnlocksOnce()
    {
        var handle = CreateHandle();
        await handle.ReleaseAsync();
        await handle.ReleaseAsync();

        await _daprClient.Received(1).Unlock(StoreName, LockKey, Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_ReleasesLock()
    {
        var handle = CreateHandle();
        await handle.DisposeAsync();

        await _daprClient.Received(1).Unlock(StoreName, LockKey, Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_DoubleDispose_Safe()
    {
        var handle = CreateHandle();
        await handle.DisposeAsync();
        await handle.DisposeAsync();

        await _daprClient.Received(1).Unlock(StoreName, LockKey, Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseAsync_ThenDispose_OnlyUnlocksOnce()
    {
        var handle = CreateHandle();
        await handle.ReleaseAsync();
        await handle.DisposeAsync();

        await _daprClient.Received(1).Unlock(StoreName, LockKey, Owner, Arg.Any<CancellationToken>());
    }
}

#pragma warning restore CS0618
