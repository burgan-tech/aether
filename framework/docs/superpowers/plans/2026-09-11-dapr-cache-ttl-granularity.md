# Dapr Cache TTL Granularity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `DaprDistributedCacheService` from turning a sub-second cache TTL into an entry that never expires.

**Architecture:** `SetAsync` currently has two divergent expiry branches that truncate the requested duration to whole seconds with an `(int)` cast. Replace both with one `requestedTtl` computation that rounds **up**, never writes a TTL below the store's one-second granularity, and skips the write entirely when the requested lifetime is already over. No public API changes — one production file plus one new unit-test class.

**Tech Stack:** .NET 10, C# `latest`, Dapr.Client 1.17.9 (`DaprClient.SaveStateAsync<TValue>` is `public abstract`, so it is substitutable), xunit + NSubstitute + Shouldly.

**Spec:** `framework/docs/superpowers/specs/2026-09-11-dapr-cache-ttl-granularity-design.md`

## Global Constraints

- Target framework `net10.0`; `LangVersion: latest` (set by `common.props`, do not override in a `.csproj`).
- `<Nullable>enable</Nullable>` with `<WarningsAsErrors>Nullable</WarningsAsErrors>` — a nullable warning fails the build.
- Never add `#pragma warning disable CS1591`; `common.props` already suppresses it globally.
- No new NuGet packages. If one were needed, its version must be declared in `Directory.Packages.props` first and the `<PackageReference>` must omit `Version`.
- Do not change `IDistributedCacheService`, `DistributedCacheBase`, `DistributedCacheEntryOptions`, `RedisDistributedCacheService`, `NetCoreDistributedCacheService`, or `AetherDistributedCacheServiceCollectionExtensions`. Those are explicitly out of scope (see the spec's Scope section).
- Build: `dotnet build framework/BBT.Aether.slnx`
- Test: `dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj`
- All commands run from the repository root: `/Users/U0B006/Documents/repos/burgan-tech/aether`.
- Work on a branch off `master`: `git checkout -b fix/dapr-cache-ttl-granularity`.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs` | Dapr state-store cache provider; owns the `ttlInSeconds` metadata contract | Modify `SetAsync` (lines 38-74), add a private `ToStoreTtlSeconds` helper |
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs` | Unit coverage for the TTL metadata the provider sends to Dapr | Create |
| `framework/docs/distributed-cache/README.md` | Feature documentation for the cache abstraction | Modify — add a provider-granularity section |

The test project (`BBT.Aether.Infrastructure.Tests`) already references `BBT.Aether.Infrastructure` and already has xunit, NSubstitute and Shouldly. No `.csproj` edits are needed anywhere in this plan.

---

### Task 1: Round the requested TTL up instead of truncating it

Replaces the two divergent expiry branches with one computation and rounds up, so a sub-second
request becomes `ttlInSeconds = 1` instead of vanishing. Non-positive requests are clamped to 1
second here — Task 2 refines them into a skipped write. Every intermediate state of this plan is
shippable: after this task an already-expired entry lives 1 second instead of forever.

**Files:**
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs:38-74`

**Interfaces:**
- Consumes: `DaprDistributedCacheService(DaprClient daprClient, string storeName)` — the existing
  primary constructor; `DistributedCacheEntryOptions.WithAbsoluteExpiration(DateTimeOffset)` and
  `.WithSlidingExpiration(TimeSpan)` from `BBT.Aether.Core`.
- Produces: `private static int ToStoreTtlSeconds(TimeSpan ttl)` inside
  `DaprDistributedCacheService` — Tasks 2 and 3 extend this exact method. The test fixture fields
  `_daprClient`, `_sut`, `_capturedMetadata` and `_saveCallCount` are reused by Tasks 2 and 3.

- [ ] **Step 1: Write the failing tests**

Create `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs`:

```csharp
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

    private sealed class CachedPayload
    {
        public string Value { get; init; } = "payload";
    }
}
```

Note on timing: `SetWithAbsoluteAsync` computes `UtcNow` in the test and `SetAsync` computes it
again, so the remaining span is a hair under the requested one — which is exactly why rounding up
produces the expected whole second. These assertions only break if more than one second of wall
clock elapses between the two reads, which cannot happen with a substituted `DaprClient`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DaprDistributedCacheServiceTests"
```

Expected: 5 failures, 1 pass.
- `SetAsync_SubSecondAbsoluteExpiration_WritesOneSecondTtl` — `_capturedMetadata` has no
  `ttlInSeconds` key (this is the reported bug).
- `SetAsync_FractionalAbsoluteExpiration_RoundsUp` — value is `"1"`, not `"2"` (truncation).
- `SetAsync_WholeSecondAbsoluteExpiration_KeepsTheRequestedValue` — value is `"59"`, not `"60"`.
  The remaining span is always a hair under 60 s, and truncation always loses that hair.
- `SetAsync_SubSecondSlidingExpiration_WritesOneSecondTtl` — value is `"0"`, not `"1"`.
- `SetAsync_AbsoluteAndSlidingBothSet_AbsoluteWins` — value is `"9"`, not `"10"` (truncation).

`SetAsync_NoOptions_WritesWithoutTtlMetadata` passes already — it is the regression guard for the
untouched path.

- [ ] **Step 3: Rewrite the TTL computation**

In `framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs`,
add `using System.Globalization;` to the using block, then replace the whole `if`/`else if` block
inside `SetAsync` (currently lines 47-63) with:

```csharp
        TimeSpan? requestedTtl = options switch
        {
            { AbsoluteExpiration: { } absolute } => absolute - DateTimeOffset.UtcNow,
            { SlidingExpiration: { } sliding } => sliding,
            _ => null,
        };

        if (requestedTtl is { } ttl)
        {
            var ttlInSeconds = ToStoreTtlSeconds(ttl);
            metadata["ttlInSeconds"] = ttlInSeconds.ToString(CultureInfo.InvariantCulture);
            activity?.SetTag("cache.ttl_seconds", ttlInSeconds);
        }
```

and add this private helper next to the other private members at the bottom of the class:

```csharp
    /// <summary>
    /// Converts a requested lifetime to the whole seconds a Dapr state store understands.
    /// </summary>
    /// <remarks>
    /// Rounding a sub-second request DOWN to zero silently turns the shortest-lived entry a caller
    /// can ask for into a permanent one, so round UP and never below one second.
    /// </remarks>
    private static int ToStoreTtlSeconds(TimeSpan ttl)
    {
        var seconds = Math.Ceiling(ttl.TotalSeconds);

        return seconds < 1 ? 1 : (int)seconds;
    }
```

The single `requestedTtl` switch is what keeps the absolute and sliding branches from drifting
apart again; it preserves the old precedence, where absolute wins when both properties are set.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DaprDistributedCacheServiceTests"
```

Expected: 6 passed, 0 failed.

- [ ] **Step 5: Verify nothing else broke**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj
```

Expected: the whole Infrastructure test suite passes.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs
git commit -m "$(cat <<'EOF'
fix(cache): round Dapr TTL up so a sub-second expiry is not stored forever

DaprDistributedCacheService truncated the requested expiry to whole seconds
and dropped the ttlInSeconds metadata when the result was zero, which Dapr
reads as "no expiry" — the shortest lifetime a caller can ask for became the
longest one. The absolute and sliding branches also disagreed on what a
sub-second request meant.

Both branches now share one computation that rounds up and never falls below
the store's one-second granularity.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Skip the write when the requested lifetime is already over

An absolute expiry in the past, or a zero/negative sliding expiration, means "this entry is already
dead". Storing it — with any TTL — is wrong; Task 1's one-second clamp is only a safe placeholder.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs` (the `if (requestedTtl is { } ttl)` block added in Task 1)
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs`

**Interfaces:**
- Consumes: `ToStoreTtlSeconds(TimeSpan)` and the `requestedTtl` switch from Task 1; the
  `_saveCallCount` and `_capturedMetadata` fixture fields from Task 1.
- Produces: no new members — an early `return` inside `SetAsync` plus a `cache.skipped` span tag.

- [ ] **Step 1: Write the failing tests**

Append these two tests to `DaprDistributedCacheServiceTests`, above the `CachedPayload` class:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DaprDistributedCacheServiceTests"
```

Expected: 2 failures, both `_saveCallCount` — expected 0 but was 1 (Task 1 writes a 1-second entry
for these inputs).

- [ ] **Step 3: Add the early return**

Replace the `if (requestedTtl is { } ttl)` block from Task 1 with:

```csharp
        if (requestedTtl is { } ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                // The caller asked for an entry that is already dead. Writing it with no TTL would
                // make it permanent — the opposite of the request — so skip the write entirely.
                activity?.SetTag("cache.skipped", true);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            var ttlInSeconds = ToStoreTtlSeconds(ttl);
            metadata["ttlInSeconds"] = ttlInSeconds.ToString(CultureInfo.InvariantCulture);
            activity?.SetTag("cache.ttl_seconds", ttlInSeconds);
        }
```

`ActivityStatusCode` is already imported via `using System.Diagnostics;` at the top of the file.
Leave the `seconds < 1 ? 1` clamp in `ToStoreTtlSeconds` — it is now unreachable from `SetAsync`,
but it keeps the helper correct on its own terms.

Skipping the write leaves an older value under the same key untouched. That is the accepted
trade-off recorded in the spec: deleting the key would cost an extra round trip and turn a
degenerate `SetAsync` into a destructive operation.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DaprDistributedCacheServiceTests"
```

Expected: 8 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs
git commit -m "$(cat <<'EOF'
fix(cache): skip the Dapr write when the requested cache lifetime is over

An absolute expiry in the past, or a non-positive sliding expiration, means the
entry is already dead. Storing it was writing a permanent entry. The write is
now skipped and the span carries cache.skipped=true.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Clamp absurdly large TTLs instead of overflowing to a negative number

`DateTimeOffset.MaxValue` is a plausible way to say "never expire". It yields roughly
2.5 × 10¹¹ seconds (about 7,973 years from today), and the unchecked `double`→`int` conversion's
behaviour on out-of-range input is unspecified: it wraps to `int.MinValue` on x64, but saturates to
`int.MaxValue` on ARM64. On x64 the provider would then send a negative `ttlInSeconds`. This defect
predates the reported bug and is not in the source report.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs` (`ToStoreTtlSeconds`)
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs`

**Interfaces:**
- Consumes: `ToStoreTtlSeconds(TimeSpan)` from Task 1.
- Produces: same signature, now total over the whole `TimeSpan` range.

- [ ] **Step 1: Write the failing test**

Append to `DaprDistributedCacheServiceTests`, above the `CachedPayload` class:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SetAsync_AbsoluteExpirationFarInTheFuture_ClampsToMaxInt"
```

Expected: FAIL on x64 — the value is `-2147483648` instead of `2147483647`. The cast's behaviour on
out-of-range input is unspecified, so this assertion may pass instead of fail on ARM64, where the
same conversion saturates to `int.MaxValue` rather than wrapping.

- [ ] **Step 3: Add the upper clamp**

Replace the body of `ToStoreTtlSeconds` with:

```csharp
    private static int ToStoreTtlSeconds(TimeSpan ttl)
    {
        var seconds = Math.Ceiling(ttl.TotalSeconds);

        if (seconds < 1)
        {
            return 1;
        }

        return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DaprDistributedCacheServiceTests"
```

Expected: 9 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheServiceTests.cs
git commit -m "$(cat <<'EOF'
fix(cache): clamp very large Dapr TTLs instead of overflowing to a negative

DateTimeOffset.MaxValue as an absolute expiry wrapped the unchecked int cast to
int.MinValue, sending a negative ttlInSeconds to the state store.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Document the provider TTL granularity

The bug was invisible because nothing told a caller that the store they got has a one-second floor.
The documentation is also currently wrong in the same section: it shows an
`AbsoluteExpirationRelativeToNow` property that `DistributedCacheEntryOptions` does not have, so a
reader copying the example gets a compile error.

**Files:**
- Modify: `framework/docs/distributed-cache/README.md` (the `## Expiration Options` section, lines 84-104)

**Interfaces:**
- Consumes: the final behaviour from Tasks 1-3.
- Produces: nothing code-facing.

- [ ] **Step 1: Fix the non-existent property in the example**

In `framework/docs/distributed-cache/README.md`, delete this block from `## Expiration Options` —
`DistributedCacheEntryOptions` exposes only `AbsoluteExpiration` and `SlidingExpiration`:

````markdown
// Relative - Expires after duration
new DistributedCacheEntryOptions
{
    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
}
````

and replace it with the supported way to express the same intent:

````markdown
// Relative - Expires after duration
new DistributedCacheEntryOptions
{
    AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(30)
}
````

- [ ] **Step 2: Add the granularity section**

Immediately after the closing fence of the `## Expiration Options` code block, add:

````markdown
### TTL granularity differs by provider

| Provider | Smallest expressible TTL | Notes |
|---|---|---|
| Redis | sub-second | the `TimeSpan` is passed through to `StringSetAsync` |
| .NET Core `IDistributedCache` | sub-second | passed through to `SetAbsoluteExpiration` |
| Dapr state store | **1 second** | Dapr's `ttlInSeconds` metadata is whole seconds |

A request below one second is rounded **up** to one second on Dapr — never down, because a TTL of
zero would make the entry permanent. Do not rely on a sub-second TTL for correctness: if a value
must disappear the moment it goes stale, invalidate it explicitly with `RemoveAsync` and keep the
TTL only as a backstop.

If the requested lifetime has already elapsed (an `AbsoluteExpiration` in the past, or a
non-positive `SlidingExpiration`), the Dapr provider skips the write rather than storing an entry
that would never expire. Any previous value under that key is left in place.
````

- [ ] **Step 3: Verify the whole solution still builds and the suite is green**

```bash
dotnet build framework/BBT.Aether.slnx
```

Expected: build succeeded, 0 errors, 0 warnings.

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add framework/docs/distributed-cache/README.md
git commit -m "$(cat <<'EOF'
docs(cache): record the per-provider TTL granularity and drop a bad example

Documents Dapr's one-second floor and the round-up behaviour, and replaces the
AbsoluteExpirationRelativeToNow example — that property does not exist on
DistributedCacheEntryOptions.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Final state of `SetAsync`

After Tasks 1-3, `SetAsync` reads:

```csharp
    public async override Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        using var activity = StartCacheActivity("DistributedCache.Set", key);

        var metadata = new Dictionary<string, string>();

        TimeSpan? requestedTtl = options switch
        {
            { AbsoluteExpiration: { } absolute } => absolute - DateTimeOffset.UtcNow,
            { SlidingExpiration: { } sliding } => sliding,
            _ => null,
        };

        if (requestedTtl is { } ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                // The caller asked for an entry that is already dead. Writing it with no TTL would
                // make it permanent — the opposite of the request — so skip the write entirely.
                activity?.SetTag("cache.skipped", true);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            var ttlInSeconds = ToStoreTtlSeconds(ttl);
            metadata["ttlInSeconds"] = ttlInSeconds.ToString(CultureInfo.InvariantCulture);
            activity?.SetTag("cache.ttl_seconds", ttlInSeconds);
        }

        await _daprClient.SaveStateAsync(
            storeName,
            key,
            value,
            metadata: metadata,
            cancellationToken: cancellationToken
        );

        activity?.SetStatus(ActivityStatusCode.Ok);
    }
```

## Release note for the PR description

This is a behaviour change for sub-second TTLs: they move from "permanent" to "1 second". Entries
already written without a TTL are **not** repaired — they expire only when overwritten. Consumers
that depend on the fix (vnext's state-function cache) should flush or key-version their entries
when the new Aether version ships.
