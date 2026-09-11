# Dapr distributed cache: sub-second TTL granularity — design

**Status:** implemented on `fix/dapr-cache-ttl-granularity`
**Area:** `BBT.Aether.Infrastructure` → `BBT/Aether/DistributedCache/Dapr/DaprDistributedCacheService.cs`
**Source issue:** vnext report `ai-docs/superpowers/reports/2026-09-11-aether-dapr-cache-ttl-granularity.md`
(filed by the vnext team from preprod runtime 0.0.92, 2026-09-10)

## Problem

`DaprDistributedCacheService.SetAsync` converts the requested expiry to whole seconds with a cast
and only writes the `ttlInSeconds` metadata when the result is greater than zero. Any expiry below
one second rounds to `0`, the metadata is omitted, and Dapr stores the entry **with no TTL at all**.
The caller asked for the shortest possible lifetime and got the longest one.

Current code (`DaprDistributedCacheService.cs:47-63`):

```csharp
if (options?.AbsoluteExpiration.HasValue == true)
{
    var ttl = (int)(options.AbsoluteExpiration.Value - DateTimeOffset.UtcNow).TotalSeconds;
    if (ttl > 0)                                   // 500 ms -> 0 -> no metadata -> no TTL
    {
        metadata["ttlInSeconds"] = ttl.ToString();
        activity?.SetTag("cache.ttl_seconds", ttl);
    }
}
else if (options?.SlidingExpiration.HasValue == true)
{
    var ttl = (int)options.SlidingExpiration.Value.TotalSeconds;
    metadata["ttlInSeconds"] = ttl.ToString();     // 500 ms -> writes "0"
    activity?.SetTag("cache.ttl_seconds", ttl);
}
```

Four defects in nine lines:

1. **Truncation, not rounding.** `(int)1.9 == 1`; a 1.9 s entry expires 47 % early.
2. **Sub-second absolute expiry silently loses its TTL.** The `ttl > 0` guard drops the metadata,
   which Dapr reads as "no expiry". This is the severe case — a data-freshness bug with no error,
   no log and no span tag.
3. **The absolute and sliding branches disagree.** Sub-second sliding writes the literal
   `ttlInSeconds: 0` instead of dropping it. The same requested duration gets two different
   meanings depending on which property the caller set.
4. **Large TTLs overflow.** `DateTimeOffset.MaxValue` as an absolute expiry yields ≈ 2.5 × 10¹¹
   seconds (about 7,973 years from today). The unchecked `(int)` cast's behaviour on out-of-range
   input is unspecified: it wraps to `int.MinValue` on x64, but saturates to `int.MaxValue` on
   ARM64. (Not in the original report — found while reading the code.)

The Redis and .NET Core providers pass the `TimeSpan` through unchanged, so the defect is invisible
in local development (Redis) and only appears where a Dapr state store is configured.

| Provider | Sub-second TTL | Behaviour |
|---|---|---|
| `RedisDistributedCacheService` | honoured | `StringSetAsync(key, value, expiry)` takes the `TimeSpan` |
| `NetCoreDistributedCacheService` | honoured | `SetAbsoluteExpiration(TimeSpan)` |
| `DaprDistributedCacheService` | **lost** | `ttlInSeconds` metadata omitted, entry is permanent |

### Production evidence (from the source report)

vnext caches a state-function response for a deliberately short window (500 ms default). On preprod
the entry never expired: written 2026-09-10 21:39:27, still served 142 seconds later at 21:41:50
with `cache_hit=true`, carrying a status the instance had already left. The client acted on the
stale body and received a 404. Trace ids `5c80e520b1b5c79e4a80d697c980e866`,
`0dbc92d9a7b2015c6daef80fff232272`.

Lowering the caller's TTL to 1 ms changed nothing — 1 ms rounds to 0 exactly like 500 ms does. No
configuration value below 1000 ms can express what the caller means, which is what makes this an
Aether-level defect rather than a consumer tuning problem.

## Decision

Round **up**, never below the store's granularity, and treat a non-positive requested TTL as a
non-write rather than as a permanent entry.

```csharp
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
        // The caller asked for an entry that is already dead. Writing it with no TTL would make it
        // permanent — the opposite of the request — so skip the write entirely.
        activity?.SetTag("cache.skipped", true);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return;
    }

    var ttlInSeconds = ToStoreTtlSeconds(ttl);
    metadata["ttlInSeconds"] = ttlInSeconds.ToString(CultureInfo.InvariantCulture);
    activity?.SetTag("cache.ttl_seconds", ttlInSeconds);
}

// Dapr state stores express TTL in whole seconds. Rounding a sub-second request DOWN to zero
// silently turns the shortest-lived entry a caller can ask for into a permanent one, so round UP
// and never below one second.
private static int ToStoreTtlSeconds(TimeSpan ttl)
{
    var seconds = Math.Ceiling(ttl.TotalSeconds);
    if (seconds < 1) return 1;
    return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
}
```

The single `requestedTtl` switch replaces the two divergent branches, so defect 3 cannot recur:
absolute and sliding now reach the same code. Absolute still wins over sliding when both are set,
matching today's `if`/`else if` precedence.

## Scope

**In scope.** `DaprDistributedCacheService.SetAsync` and a new unit-test class covering it, plus a
provider-granularity note in `framework/docs/distributed-cache/README.md`.

**Out of scope — deliberately.**

- **`IDistributedCacheService.MinimumTtl`.** The source report recommends exposing store
  granularity on the abstraction (`TimeSpan.Zero` for Redis/in-memory, `TimeSpan.FromSeconds(1)`
  for Dapr). That adds a member to a published interface in `BBT.Aether.Core` and touches all three
  providers. Worth doing, but as its own change with its own design — it does not gate this fix.
- **A `Debug` log when a TTL is rounded up.** `InfrastructureActivitySource.StartDiagnosticActivity`
  only returns an activity when the process-wide tracing profile is `Verbose`
  (`AetherTracingRuntime`, default `Business` — see `framework/docs/telemetry/README.md`). Under the
  default `Business` profile `activity` is `null`, every `SetTag` is a no-op, and a skipped write
  produces no log, no metric and no span — the `cache.ttl_seconds` and `cache.skipped` tags are not
  a substitute for a log in a default deployment. The logger was still deferred: adding a parameter
  to `DaprDistributedCacheService`'s primary constructor is a binary-breaking change for a type
  shipped in a published NuGet package, and the approved scope for this change was the Dapr
  provider and its tests. The consequence is real — an operator investigating "nothing is ever
  cached" has no default-profile signal to go on. Giving the provider an optional `ILogger`
  (`RedisDistributedCacheService` already takes one) is recommended follow-up work, alongside the
  `MinimumTtl` item above.
- **Cross-provider alignment of already-expired writes.** The three providers currently disagree:
  Dapr writes a permanent entry (this bug), Redis passes a negative `TimeSpan` to
  `StringSetAsync`, .NET Core writes an entry that is immediately expired. Aligning them is a
  separate behavioural change across two more files.
- **A Redis/Dapr parity integration test.** The report asks for "a 500 ms entry is gone after 2 s"
  asserted on both providers. That needs a live Redis plus a Dapr sidecar; CI has neither today.

## Deviations from the source report's proposal

1. **Non-positive `SlidingExpiration` also skips the write.** The report's snippet guards the
   sliding branch with `sliding > TimeSpan.Zero` and then falls through to a write with empty
   metadata — i.e. a *permanent* entry, which perpetuates the report's own defect 3. Here both
   branches share one rule: requested ttl ≤ 0 → no write.
2. **`cache.ttl_seconds` stays an `int` tag.** The report's snippet changes it to the string pulled
   back out of the metadata dictionary. `RedisDistributedCacheService` emits an int; keeping the
   types equal keeps the two providers' spans comparable.
3. **A `cache.skipped` tag marks the skipped write.** The report leaves the skip silent. The
   activity already exists, so the tag is free.
4. **Upper clamp at `int.MaxValue`** (defect 4), which the report does not cover.

## Accepted consequence

When a caller passes an already-expired absolute expiry and the key already holds an older value,
skipping the write **leaves that older value in place**. Deleting the key instead would be
defensible, but it costs an extra round trip and turns a degenerate `SetAsync` into a destructive
operation. This is not only a caller bug or a clock race: `DistributedCacheBase.GetOrSetAsync`
builds the caller's `options` — including any absolute expiry — before it awaits `fetchFunc()`, and
calls `SetAsync` only afterwards, so a short absolute expiry paired with a slow fetch is a normal
way to reach this path. Leaving the previous entry to expire on its own terms is still the smaller
surprise than deleting it.

## Compatibility

- Callers using second-or-larger TTLs are unaffected except for the truncation fix: a 1.9 s entry
  now expires at 2 s instead of 1 s — closer to the request, and never earlier than asked.
- Callers using sub-second TTLs move from "permanent" to "1 second". That is a behaviour change,
  and it is the point: today they hold stale data indefinitely.
- Entries already written without a TTL are **not** repaired by this change; they expire only when
  overwritten. Consumers depending on the fix (vnext's state-function cache) should flush or
  key-version their entries when the new Aether version is rolled out. This belongs in the release
  note.
- No public API changes, so no consumer recompilation is required.

## Test plan

Unit tests only, `framework/test/BBT.Aether.Infrastructure.Tests`, using the existing
xunit + NSubstitute + Shouldly stack. `DaprClient.SaveStateAsync<TValue>` is `public abstract`
(verified against Dapr.Client 1.17.9), so `Substitute.For<DaprClient>()` can capture the metadata
dictionary — the same approach `DaprDistributedLockServiceTests` already uses.

| Requested | Expected |
|---|---|
| `AbsoluteExpiration = now + 500 ms` | `ttlInSeconds = "1"` |
| `AbsoluteExpiration = now + 1.4 s` | `ttlInSeconds = "2"` |
| `AbsoluteExpiration = now + 60 s` | `ttlInSeconds = "60"` |
| `AbsoluteExpiration = now - 5 s` | no `SaveStateAsync` call |
| `SlidingExpiration = 500 ms` | `ttlInSeconds = "1"` |
| `SlidingExpiration = TimeSpan.Zero` | no `SaveStateAsync` call |
| `AbsoluteExpiration = DateTimeOffset.MaxValue` | `ttlInSeconds = "2147483647"` |
| both absolute and sliding set | absolute wins |
| no options | write happens, no `ttlInSeconds` key |

## Implementation plan

`framework/docs/superpowers/plans/2026-09-11-dapr-cache-ttl-granularity.md`
