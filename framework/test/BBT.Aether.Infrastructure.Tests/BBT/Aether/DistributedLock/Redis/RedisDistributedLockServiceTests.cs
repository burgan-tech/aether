using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock.Redis;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace BBT.Aether.DistributedLock.Redis;

public class RedisDistributedLockServiceTests
{
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly ILogger<RedisDistributedLockService> _logger = Substitute.For<ILogger<RedisDistributedLockService>>();
    private readonly IApplicationInfoAccessor _appInfo = Substitute.For<IApplicationInfoAccessor>();
    private readonly RedisDistributedLockService _sut;

    public RedisDistributedLockServiceTests()
    {
        _appInfo.ApplicationName.Returns("test-app");
        _appInfo.InstanceId.Returns("inst-1");
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_database);
        _sut = new RedisDistributedLockService(_redis, _logger, _appInfo);
    }

    private void SetupStringSetReturns(bool result)
    {
        _database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .Returns(result);
    }

    [Fact]
    public async Task TryAcquireLockAsync_Success_ReturnsHandle()
    {
        SetupStringSetReturns(true);

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldNotBeNull();
        handle.LockKey.ShouldBe("resource:1");
        handle.Owner.ShouldNotBeNullOrWhiteSpace();

        await handle.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireLockAsync_Failure_ReturnsNull()
    {
        SetupStringSetReturns(false);

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldBeNull();
    }

    [Fact]
    public async Task TryAcquireLockAsync_GeneratesUniqueOwnerPerCall()
    {
        SetupStringSetReturns(true);
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle1 = await _sut.TryAcquireLockAsync("resource:1", 30);
        var handle2 = await _sut.TryAcquireLockAsync("resource:2", 30);

        handle1!.Owner.ShouldNotBe(handle2!.Owner);

        await handle1.DisposeAsync();
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireLockAsync_Exception_ReturnsNull()
    {
        _database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .Returns<bool>(x => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "fail"));

        var handle = await _sut.TryAcquireLockAsync("resource:1", 30);

        handle.ShouldBeNull();
    }
}
