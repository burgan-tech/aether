using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock.Dapr;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

#pragma warning disable CS0618

namespace BBT.Aether.DistributedLock.Dapr;

public class DaprDistributedLockServiceTests
{
    private const string StoreName = "lockstore";
    private readonly DaprClient _daprClient = Substitute.For<DaprClient>();
    private readonly ILogger<DaprDistributedLockService> _logger = Substitute.For<ILogger<DaprDistributedLockService>>();
    private readonly IApplicationInfoAccessor _appInfo = Substitute.For<IApplicationInfoAccessor>();
    private readonly DaprDistributedLockService _sut;

    public DaprDistributedLockServiceTests()
    {
        _appInfo.ApplicationName.Returns("test-app");
        _sut = new DaprDistributedLockService(_daprClient, _logger, _appInfo, StoreName);
    }

    private static TryLockResponse CreateLockResponse(bool success)
    {
        var response = new TryLockResponse { Success = success };
        return response;
    }

    [Fact]
    public async Task TryAcquireLockAsync_Success_ReturnsHandle()
    {
        _daprClient.Lock(StoreName, "resource:1", Arg.Any<string>(), 30, Arg.Any<CancellationToken>())
            .Returns(CreateLockResponse(true));

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldNotBeNull();
        handle.LockKey.ShouldBe("resource:1");
        handle.Owner.ShouldNotBeNullOrWhiteSpace();

        await handle.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireLockAsync_Failure_ReturnsNull()
    {
        _daprClient.Lock(StoreName, "resource:1", Arg.Any<string>(), 30, Arg.Any<CancellationToken>())
            .Returns(CreateLockResponse(false));

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldBeNull();
    }

    [Fact]
    public async Task TryAcquireLockAsync_NullResponse_ReturnsNull()
    {
        _daprClient.Lock(StoreName, "resource:1", Arg.Any<string>(), 30, Arg.Any<CancellationToken>())
            .Returns((TryLockResponse?)null);

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldBeNull();
    }

    [Fact]
    public async Task TryAcquireLockAsync_Exception_ReturnsNull()
    {
        _daprClient.Lock(StoreName, "resource:1", Arg.Any<string>(), 30, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("connection failed"));

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldBeNull();
    }

    [Fact]
    public async Task TryAcquireLockAsync_GeneratesUniqueOwnerPerCall()
    {
        _daprClient.Lock(StoreName, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(CreateLockResponse(true));

        var handle1 = await _sut.TryAcquireLockAsync("resource:1", 30);
        var handle2 = await _sut.TryAcquireLockAsync("resource:2", 30);

        handle1!.Owner.ShouldNotBe(handle2!.Owner);

        await handle1.DisposeAsync();
        await handle2.DisposeAsync();
    }
}

#pragma warning restore CS0618
