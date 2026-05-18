using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace BBT.Aether.DistributedLock.Redis;

public class RedisDistributedLockHandleTests
{
    private const string LockKey = "test:resource";
    private const string Owner = "machine:def456";
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    private RedisDistributedLockHandle CreateHandle() =>
        new(_database, LockKey, Owner, _logger);

    [Fact]
    public void Properties_ReturnsCorrectValues()
    {
        var handle = CreateHandle();

        handle.LockKey.ShouldBe(LockKey);
        handle.Owner.ShouldBe(Owner);
    }

    [Fact]
    public async Task ExtendAsync_Success_ReturnsTrue()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        var result = await handle.ExtendAsync(120);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ExtendAsync_OwnerMismatch_ReturnsFalse()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)0L));

        var handle = CreateHandle();
        var result = await handle.ExtendAsync(120);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtendAsync_AfterDispose_ReturnsFalse()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        await handle.DisposeAsync();

        var result = await handle.ExtendAsync(120);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtendAsync_Exception_ReturnsFalse()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "connection failed"));

        var handle = CreateHandle();
        var result = await handle.ExtendAsync(120);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_CallsScriptEvaluate()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        await handle.ReleaseAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ReleaseAsync_DoubleRelease_OnlyCallsOnce()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        await handle.ReleaseAsync();
        await handle.ReleaseAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DisposeAsync_ReleasesLock()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        await handle.DisposeAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DisposeAsync_DoubleDispose_Safe()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        await handle.DisposeAsync();
        await handle.DisposeAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ReleaseAsync_ThenDispose_OnlyReleasesOnce()
    {
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        var handle = CreateHandle();
        await handle.ReleaseAsync();
        await handle.DisposeAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>());
    }
}
