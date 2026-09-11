using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.DistributedCache.Dapr;

public class DaprDistributedCacheServiceTests
{
    private const string StoreName = "statestore";
    private const string Key = "product:1";

    private readonly DaprClient _daprClient = Substitute.For<DaprClient>();
    private readonly DaprDistributedCacheService _sut;

    private IReadOnlyDictionary<string, string>? _capturedMetadata;
    private int _saveCallCount;

    public DaprDistributedCacheServiceTests()
    {
        // DaprClient.SaveStateAsync<TValue> is public abstract, so NSubstitute can intercept it and
        // hand us the metadata dictionary the provider built. Argument 4 is `metadata`.
        _daprClient
            .When(c => c.SaveStateAsync(
                StoreName,
                Arg.Any<string>(),
                Arg.Any<CachedPayload>(),
                Arg.Any<StateOptions?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                _saveCallCount++;
                _capturedMetadata = call.ArgAt<IReadOnlyDictionary<string, string>?>(4);
            });

        _sut = new DaprDistributedCacheService(_daprClient, StoreName);
    }

    private Task SetWithAbsoluteAsync(TimeSpan fromNow)
        => _sut.SetAsync(
            Key,
            new CachedPayload(),
            DistributedCacheEntryOptions.WithAbsoluteExpiration(DateTimeOffset.UtcNow.Add(fromNow)));

    private Task SetWithSlidingAsync(TimeSpan sliding)
        => _sut.SetAsync(
            Key,
            new CachedPayload(),
            DistributedCacheEntryOptions.WithSlidingExpiration(sliding));

    [Fact]
    public async Task SetAsync_SubSecondAbsoluteExpiration_WritesOneSecondTtl()
    {
        await SetWithAbsoluteAsync(TimeSpan.FromMilliseconds(500));

        _saveCallCount.ShouldBe(1);
        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!.ShouldContainKey("ttlInSeconds");
        _capturedMetadata["ttlInSeconds"].ShouldBe("1");
    }

    [Fact]
    public async Task SetAsync_FractionalAbsoluteExpiration_RoundsUp()
    {
        await SetWithAbsoluteAsync(TimeSpan.FromMilliseconds(1400));

        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!["ttlInSeconds"].ShouldBe("2");
    }

    [Fact]
    public async Task SetAsync_WholeSecondAbsoluteExpiration_KeepsTheRequestedValue()
    {
        await SetWithAbsoluteAsync(TimeSpan.FromSeconds(60));

        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!["ttlInSeconds"].ShouldBe("60");
    }

    [Fact]
    public async Task SetAsync_SubSecondSlidingExpiration_WritesOneSecondTtl()
    {
        await SetWithSlidingAsync(TimeSpan.FromMilliseconds(500));

        _saveCallCount.ShouldBe(1);
        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!["ttlInSeconds"].ShouldBe("1");
    }

    [Fact]
    public async Task SetAsync_AbsoluteAndSlidingBothSet_AbsoluteWins()
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(10),
            SlidingExpiration = TimeSpan.FromSeconds(600),
        };

        await _sut.SetAsync(Key, new CachedPayload(), options);

        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!["ttlInSeconds"].ShouldBe("10");
    }

    [Fact]
    public async Task SetAsync_NoOptions_WritesWithoutTtlMetadata()
    {
        await _sut.SetAsync(Key, new CachedPayload());

        _saveCallCount.ShouldBe(1);
        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!.ShouldNotContainKey("ttlInSeconds");
    }

    [Fact]
    public async Task SetAsync_AbsoluteExpirationAlreadyPassed_DoesNotWrite()
    {
        await SetWithAbsoluteAsync(TimeSpan.FromSeconds(-5));

        _saveCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task SetAsync_NonPositiveSlidingExpiration_DoesNotWrite()
    {
        await SetWithSlidingAsync(TimeSpan.Zero);

        _saveCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task SetAsync_AbsoluteExpirationFarInTheFuture_ClampsToMaxInt()
    {
        await _sut.SetAsync(
            Key,
            new CachedPayload(),
            DistributedCacheEntryOptions.WithAbsoluteExpiration(DateTimeOffset.MaxValue));

        _capturedMetadata.ShouldNotBeNull();
        _capturedMetadata!["ttlInSeconds"].ShouldBe("2147483647");
    }

    private sealed class CachedPayload
    {
        public string Value { get; init; } = "payload";
    }
}
