# Outbox Faz 2 — Wake-up Signal

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Outbox dispatcher'ının boş poll'lerini ortadan kaldırmak — commit sonrası best-effort bir Dapr sinyali worker'ı uyandırsın, böylece fallback aralığı dakikalara çıkarılabilsin ve latency yine de saniye altında kalsın.

**Architecture:** Yazma yolu (`EfCoreOutboxStore`) scoped bir collector'a `(schema, partition)` çifti işaretler; collector `IUnitOfWork.OnCompleted` ile commit sonrasında benzersiz çiftleri Dapr'a publish eder. Worker tarafında `[HttpPost]` bir endpoint sinyali alıp singleton bir coordinator'ı uyandırır; `OutboxBackgroundService` `Task.Delay` yerine `coordinator.WaitAsync(fallbackInterval)` bekler. Sinyal **güvenilir mesaj değildir** — kaybolursa yalnızca latency artar, fallback polling correctness mekanizması olarak her zaman açık kalır.

**Tech Stack:** .NET 10, Dapr pub/sub (Redis Streams), `System.Threading.Channels`, xUnit + Shouldly + NSubstitute, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-07-28-outbox-signal-partition-design.md` §6 Faz 2

**Branches (ikisi de Faz 1 ucundan dallandı, mevcut):**
- Aether: `feature/outbox-faz2-signal` @ `/Users/U0B006/Documents/repos/burgan-tech/aether`
- vNext: `feature/outbox-faz2-signal` @ `/Users/U0B006/Documents/repos/burgan-tech/vnext`

> ⚠️ **vNext'te ilk commit `a7797dae` local-link'tir** (Aether'ı `ProjectReference` ile bağlar,
> `AetherPackageVersion`'ı 1.0.33'e döndürür). CI'da derlenmez. **PR açmadan önce revert edilmeli.**
> Bu sayede Faz 2, yayımlanmamış Aether değişikliklerine karşı geliştirilebiliyor.

---

## Spec'ten sapmalar (bilinçli, gerekçeli)

Spec §2.1 sinyal kontratında `RuntimeKey` diyordu. **`Schema` olarak değiştirildi.**
Aether SDK'da "runtime" diye bir kavram yok; sinyalin işaret ettiği şey bir outbox
tablosudur ve processor zaten kendini `options.Schema` ile kapsıyor. Worker gelen sinyalin
şemasını kendi şemasıyla karşılaştırıp eşleşmeyeni yok sayar. Domain izolasyonu zaten
transport'tan geliyor (Redis pubsub domain başına ayrı).

Spec §2.5 `[Topic]` attribute'u öneriyordu. **Declarative Subscription YAML kullanılacak** —
vNext'te hiç `[Topic]` yok, tüm subscription'lar `etc/workers/*/dapr/components/*.yaml`
üzerinden tanımlı. Mevcut desene uyuluyor.

Spec §2.7 `FallbackPollingEnabled` diye bir flag öngörüyordu ama "her zaman `true`" diyordu.
**Eklenmiyor.** Her zaman true olması gereken şey opsiyon değildir; kapatılabilir olması
yalnızca birinin yanlışlıkla correctness mekanizmasını kapatmasına imkân verirdi. Fallback
polling bu tasarımda koşulsuz: `WaitAsync` sinyal gelmezse timeout ile döner ve döngü poll
eder. Kapatmanın yolu yok, olmamalı da.

Spec §2.9 health ayrımı istiyordu (sinyal bozukken worker unready yapılmasın; degraded
alarmı). **Readiness'a hiç dokunulmuyor** — yani "sinyal bozukken unready olmasın" şartı
zaten sağlanıyor, çünkü sinyalle ilişkili bir health check eklenmiyor. Degraded alarmı için
gereken sinyaller mevcut: publisher başarısızlıkta `LogWarning` yazıyor ve
`Outbox.Signal.Publish` span'i hata durumuyla işaretleniyor. **Sayaç metriği eklenmiyor** —
Faz 1'in son review'ında da not edildiği gibi observability ayrı bir iş kalemi; T10 latency
kazancını elle ölçüyor.

---

## File Structure

### Aether — yeni

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxWakeupSignal.cs` | Sinyal kontratı (record) | T1 |
| `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxWakeupPublisher.cs` | Publisher sözleşmesi | T1 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxWakeupPublisher.cs` | No-op varsayılan (Dapr'sız tüketiciler) | T1 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxSignalCollector.cs` | Transaction başına coalescing sözleşmesi | T2 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/OutboxSignalCollector.cs` | Scoped collector + `OnCompleted` hook | T2 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxSignalCollector.cs` | No-op collector for the legacy store constructor | T4 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Distributed/DaprOutboxWakeupPublisher.cs` | Best-effort Dapr publish | T3 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/IOutboxSignalCoordinator.cs` | Worker uyandırma sözleşmesi | T5 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/OutboxSignalCoordinator.cs` | Channel + pending set | T5 |

### Aether — değiştirilecek

| Dosya | Değişiklik | Görev |
|---|---|---|
| `.../BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs` | `SignalEnabled`, `SignalTopic`, `SignalTtlSeconds` | T1 |
| `.../BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs` | Collector'a işaretle | T4 |
| `.../BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs` | `coordinator.WaitAsync` | T6 |
| `.../AetherOutboxServiceCollectionExtensions.cs` | DI kayıtları | T7 |

### Aether — testler

| Dosya | Görev |
|---|---|
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/OutboxSignalCollectorTests.cs` | T2 |
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxSignalCoordinatorTests.cs` | T5 |
| `framework/test/BBT.Aether.Postgres.Tests/OutboxSignalIntegrationTests.cs` | T4 |

### vNext

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `workers/BBT.Workflow.Workers.Outbox/Controllers/OutboxWakeupController.cs` | Subscription endpoint | T8 |
| `etc/workers/outbox/dapr/components/outbox-wakeup-subscription.yaml` | Dapr routing | T9 |
| `workers/BBT.Workflow.Workers.Outbox/appsettings.json` | Sinyal flag'leri | T9 |

---

## Task 1: Sinyal kontratı ve options

**Files:**
- Create: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxWakeupSignal.cs`
- Create: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxWakeupPublisher.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxWakeupPublisher.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs`

- [ ] **Step 1: Sinyal record'unu oluştur**

`OutboxWakeupSignal.cs`:

```csharp
using System;

namespace BBT.Aether.Events;

/// <summary>
/// A hint that an outbox table may contain dispatchable rows.
/// </summary>
/// <remarks>
/// <para>
/// This is NOT a reliable message. It may be lost, duplicated, or delivered late. Losing one
/// only delays publishing by the dispatcher's fallback interval; it never loses data. The
/// outbox table remains the source of truth and fallback polling remains the reconciliation
/// mechanism.
/// </para>
/// <para>
/// Carries no business payload, no credentials and no message identifiers — only enough to
/// point a worker at the right table and partition.
/// </para>
/// </remarks>
/// <param name="Schema">The outbox schema the rows were written to.</param>
/// <param name="PartitionId">Logical partition of the written rows, or -1 meaning "check all".</param>
/// <param name="Source">Who emitted the signal. Telemetry only — never changes worker behaviour.</param>
/// <param name="EmittedAt">When the signal was emitted. Telemetry only.</param>
public sealed record OutboxWakeupSignal(
    string Schema,
    short PartitionId,
    string? Source = null,
    DateTimeOffset? EmittedAt = null)
{
    /// <summary>Sentinel <see cref="PartitionId"/> meaning "check every partition".</summary>
    public const short AllPartitions = -1;
}
```

- [ ] **Step 2: Publisher sözleşmesini oluştur**

`IOutboxWakeupPublisher.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes outbox wake-up signals. Implementations must be best-effort.
/// </summary>
public interface IOutboxWakeupPublisher
{
    /// <summary>
    /// Attempts to publish a wake-up signal. Returns false on failure instead of throwing —
    /// the business transaction has already committed by this point and must not be failed by
    /// a broker problem. Only <see cref="System.OperationCanceledException"/> for a cancelled
    /// caller token may propagate.
    /// </summary>
    Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: No-op varsayılanı oluştur**

Aether'ı Dapr'sız tüketenler için. `NullOutboxWakeupPublisher.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

/// <summary>
/// Default publisher used when no broker-backed implementation is registered.
/// Signals are simply dropped; the dispatcher's fallback polling still finds the rows.
/// </summary>
public sealed class NullOutboxWakeupPublisher : IOutboxWakeupPublisher
{
    public Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
```

- [ ] **Step 4: Options'a sinyal alanlarını ekle**

`AetherOutboxOptions.cs` içine, `PartitionCount`'un altına:

```csharp
    /// <summary>
    /// Whether the write path publishes wake-up signals after commit.
    /// Ship disabled and enable per environment; the dispatcher works either way.
    /// </summary>
    public bool SignalEnabled { get; set; }

    /// <summary>Pub/sub topic wake-up signals are published to.</summary>
    public string SignalTopic { get; set; } = "outbox-wakeup";

    /// <summary>
    /// Time-to-live applied to a published signal. Short by design — a stale wake-up has no
    /// value, since fallback polling will have covered the work by then.
    /// </summary>
    public int SignalTtlSeconds { get; set; } = 30;
```

`SignalEnabled` varsayılanı `false` — kademeli rollout için.

- [ ] **Step 5: Derle**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
dotnet build
```

Beklenen: 0 error.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxWakeupSignal.cs \
        framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxWakeupPublisher.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxWakeupPublisher.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs
git commit -m "feat(outbox): add wake-up signal contract and options

The signal is a hint, not a reliable message: losing one only delays publishing
by the fallback interval. Ships disabled by default."
```

---

## Task 2: Transaction başına coalescing collector

**Neden:** Tek bir transaction aynı partition'a 100 outbox satırı yazabilir. 100 sinyal
göndermek broker'ı gereksiz yorar; **1 sinyal** yeterli. Ayrıca sinyal **commit'ten sonra**
gönderilmeli — önce gönderilirse worker uyanır, satırı göremez (henüz görünür değil ya da
rollback olur) ve boş bir poll harcar.

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxSignalCollector.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/OutboxSignalCollector.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/OutboxSignalCollectorTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OutboxSignalCollectorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events;

public sealed class OutboxSignalCollectorTests
{
    private sealed class RecordingPublisher : IOutboxWakeupPublisher
    {
        public List<OutboxWakeupSignal> Published { get; } = [];
        public bool ThrowOnPublish { get; set; }

        public Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("broker down");
            Published.Add(signal);
            return Task.FromResult(true);
        }
    }

    /// <summary>Captures the OnCompleted handler so the test can fire it like a real commit.</summary>
    private static (IUnitOfWorkManager Manager, Func<Task> FireCommit) FakeUow()
    {
        Func<IUnitOfWork, Task>? handler = null;
        var uow = Substitute.For<IUnitOfWork>();
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(h => handler = h))
           .Returns(Substitute.For<IDisposable>());

        var manager = Substitute.For<IUnitOfWorkManager>();
        manager.Current.Returns(uow);

        return (manager, () => handler is null ? Task.CompletedTask : handler(uow));
    }

    private static AetherOutboxOptions Options(bool enabled = true) =>
        new() { Schema = "sys_queues", SignalEnabled = enabled };

    [Fact]
    public async Task Many_rows_in_one_transaction_produce_one_signal_per_partition()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = new OutboxSignalCollector(manager, publisher, Options());

        for (var i = 0; i < 100; i++) collector.Mark("sys_queues", 7);

        publisher.Published.ShouldBeEmpty();   // nothing before commit
        await fireCommit();

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].Schema.ShouldBe("sys_queues");
        publisher.Published[0].PartitionId.ShouldBe((short)7);
    }

    [Fact]
    public async Task Distinct_partitions_each_get_their_own_signal()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = new OutboxSignalCollector(manager, publisher, Options());

        collector.Mark("sys_queues", 1);
        collector.Mark("sys_queues", 2);
        collector.Mark("sys_queues", 1);

        await fireCommit();

        publisher.Published.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Nothing_is_published_when_the_transaction_never_commits()
    {
        var publisher = new RecordingPublisher();
        var (manager, _) = FakeUow();
        var collector = new OutboxSignalCollector(manager, publisher, Options());

        collector.Mark("sys_queues", 3);
        // commit handler deliberately not fired — simulates rollback

        publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_publisher_does_not_escape_the_commit_handler()
    {
        // The business transaction has already committed. A broker problem must never surface
        // as an exception here, or a successful commit would look like a failed request.
        var publisher = new RecordingPublisher { ThrowOnPublish = true };
        var (manager, fireCommit) = FakeUow();
        var collector = new OutboxSignalCollector(manager, publisher, Options());

        collector.Mark("sys_queues", 4);

        await Should.NotThrowAsync(fireCommit);
    }

    [Fact]
    public async Task Marking_is_inert_when_signalling_is_disabled()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = new OutboxSignalCollector(manager, publisher, Options(enabled: false));

        collector.Mark("sys_queues", 5);
        await fireCommit();

        publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Too_many_partitions_collapse_into_a_single_check_all_signal()
    {
        var publisher = new RecordingPublisher();
        var (manager, fireCommit) = FakeUow();
        var collector = new OutboxSignalCollector(manager, publisher, Options());

        for (short p = 0; p < 40; p++) collector.Mark("sys_queues", p);

        await fireCommit();

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].PartitionId.ShouldBe(OutboxWakeupSignal.AllPartitions);
    }
}
```

- [ ] **Step 2: Testi çalıştır, derlenmediğini gör**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxSignalCollectorTests"
```

Beklenen: `CS0246: The type or namespace name 'OutboxSignalCollector' could not be found`.

- [ ] **Step 3: Sözleşmeyi oluştur**

`IOutboxSignalCollector.cs`:

```csharp
namespace BBT.Aether.Events;

/// <summary>
/// Collects wake-up signals produced during one unit of work and publishes a coalesced set
/// after it commits.
/// </summary>
/// <remarks>
/// Scoped: one instance per unit of work. Marking is cheap and idempotent — a transaction
/// writing a hundred rows to one partition yields a single signal.
/// </remarks>
public interface IOutboxSignalCollector
{
    /// <summary>Records that a row was written to the given schema and partition.</summary>
    void Mark(string schema, short partitionId);
}
```

- [ ] **Step 4: Collector'ı oluştur**

`OutboxSignalCollector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Uow;

namespace BBT.Aether.Events;

/// <summary>
/// Coalesces wake-up signals per unit of work and publishes them after commit.
/// </summary>
public sealed class OutboxSignalCollector(
    IUnitOfWorkManager unitOfWorkManager,
    IOutboxWakeupPublisher publisher,
    AetherOutboxOptions options) : IOutboxSignalCollector
{
    /// <summary>
    /// Above this many distinct partitions in one transaction, a single check-all signal is
    /// cheaper for the broker than one signal each.
    /// </summary>
    private const int CollapseThreshold = 16;

    private readonly HashSet<(string Schema, short PartitionId)> _pending = [];
    private bool _hookRegistered;

    public void Mark(string schema, short partitionId)
    {
        if (!options.SignalEnabled) return;

        _pending.Add((schema, partitionId));
        RegisterCommitHookOnce();
    }

    private void RegisterCommitHookOnce()
    {
        if (_hookRegistered) return;

        var uow = unitOfWorkManager.Current;
        if (uow is null) return;   // no ambient transaction; nothing to hook

        uow.OnCompleted(_ => PublishPendingAsync());
        _hookRegistered = true;
    }

    private async Task PublishPendingAsync()
    {
        if (_pending.Count == 0) return;

        var signals = BuildSignals();
        _pending.Clear();

        foreach (var signal in signals)
        {
            try
            {
                await publisher.TryPublishAsync(signal).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The business transaction has already committed. A broker failure must not
                // surface here — fallback polling covers the rows regardless.
            }
        }
    }

    private List<OutboxWakeupSignal> BuildSignals()
    {
        var emittedAt = DateTimeOffset.UtcNow;

        var distinctSchemas = _pending.Select(p => p.Schema).Distinct().ToList();
        var collapse = _pending.Count > CollapseThreshold;

        return collapse
            ? distinctSchemas
                .Select(s => new OutboxWakeupSignal(s, OutboxWakeupSignal.AllPartitions, "application", emittedAt))
                .ToList()
            : _pending
                .Select(p => new OutboxWakeupSignal(p.Schema, p.PartitionId, "application", emittedAt))
                .ToList();
    }
}
```

> `_pending` bir `HashSet` ve collector scoped olduğu için eşzamanlı erişim beklenmiyor —
> tek bir UoW tek bir mantıksal akışta çalışır. Bu varsayım T4'ün entegrasyon testinde
> gerçek DI altında doğrulanacak.

- [ ] **Step 5: Testi çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxSignalCollectorTests"
```

Beklenen: 6 test PASS.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxSignalCollector.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/OutboxSignalCollector.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/OutboxSignalCollectorTests.cs
git commit -m "feat(outbox): coalesce wake-up signals per unit of work

One transaction writing many rows to a partition now yields a single signal,
published only after the transaction commits. A broker failure in the commit
handler is swallowed: the transaction already succeeded."
```

---

## Task 3: Dapr publisher

**Files:**
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Distributed/DaprOutboxWakeupPublisher.cs`

`BBT.Aether.Infrastructure` zaten Dapr'a bağımlı (`DaprEventBus` orada) — T8'deki
provider-coupling sorunu burada yok.

- [ ] **Step 1: Publisher'ı oluştur**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events;

/// <summary>
/// Publishes outbox wake-up signals over Dapr pub/sub, best-effort.
/// </summary>
/// <remarks>
/// Signals bypass the outbox deliberately: routing a wake-up hint through the very table it
/// is meant to drain would be circular. A failure here is logged and swallowed — the caller
/// has already committed, and fallback polling still finds the rows.
/// </remarks>
public sealed class DaprOutboxWakeupPublisher(
    DaprClient daprClient,
    AetherEventBusOptions eventBusOptions,
    AetherOutboxOptions outboxOptions,
    ILogger<DaprOutboxWakeupPublisher> logger) : IOutboxWakeupPublisher
{
    public async Task<bool> TryPublishAsync(
        OutboxWakeupSignal signal,
        CancellationToken cancellationToken = default)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "Outbox.Signal.Publish", ActivityKind.Producer, Activity.Current?.Context ?? default);

        activity?.SetTag("outbox.schema", signal.Schema);
        activity?.SetTag("outbox.partition_id", signal.PartitionId);

        try
        {
            var metadata = new Dictionary<string, string>
            {
                ["ttlInSeconds"] = outboxOptions.SignalTtlSeconds.ToString()
            };

            await daprClient.PublishEventAsync(
                eventBusOptions.PubSubName,
                outboxOptions.SignalTopic,
                signal,
                metadata,
                cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            logger.LogWarning(
                exception,
                "Outbox wake-up signal could not be published. Schema: {Schema}, PartitionId: {PartitionId}. "
                + "Fallback polling will pick the rows up.",
                signal.Schema, signal.PartitionId);
            return false;
        }
    }
}
```

`InfrastructureActivitySource` ve `AetherEventBusOptions`'ın gerçek isim alanlarını
`DaprEventBus.cs`'ten doğrula — orada aynıları kullanılıyor.

- [ ] **Step 2: Derle**

```bash
dotnet build framework/src/BBT.Aether.Infrastructure
```

Beklenen: 0 error.

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Distributed/DaprOutboxWakeupPublisher.cs
git commit -m "feat(outbox): publish wake-up signals over Dapr, best-effort

Signals bypass the outbox on purpose — routing a hint through the table it
drains would be circular. Failures are logged, never thrown."
```

---

## Task 4: Yazma yolunu collector'a bağla

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxSignalCollector.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/OutboxSignalIntegrationTests.cs`

- [ ] **Step 1: Entegrasyon testini yaz**

`NpgsqlLeaseStoreTests.cs`'teki harness'ı (`TestDbContext`, `BuildProvider`,
`SetupSchemaAsync`, `_schema`) kopyalayıp `signal_test_` ön ekiyle uyarla. `BuildProvider`
içinde `options.SignalEnabled = true` ve `IOutboxWakeupPublisher` yerine kaydeden bir sahte
kaydet:

```csharp
    private sealed class RecordingPublisher : IOutboxWakeupPublisher
    {
        public List<OutboxWakeupSignal> Published { get; } = [];
        public Task<bool> TryPublishAsync(OutboxWakeupSignal s, CancellationToken ct = default)
        {
            Published.Add(s);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Committing_outbox_rows_publishes_one_signal_per_partition()
    {
        var publisher = new RecordingPublisher();
        var sp = BuildProvider(publisher);
        await SetupSchemaAsync(sp);

        await using (var scope = sp.CreateAsyncScope())
        {
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

                // Same Subject twice → same partition → must coalesce to one signal.
                await store.StoreAsync(Envelope(subject: "instance-a"));
                await store.StoreAsync(Envelope(subject: "instance-a"));

                publisher.Published.ShouldBeEmpty();   // not before commit
                await uow.CommitAsync();
            }
        }

        publisher.Published.Count.ShouldBe(1);
        publisher.Published[0].Schema.ShouldBe(_schema);
    }

    [Fact]
    public async Task Rolling_back_publishes_no_signal()
    {
        var publisher = new RecordingPublisher();
        var sp = BuildProvider(publisher);
        await SetupSchemaAsync(sp);

        await using (var scope = sp.CreateAsyncScope())
        {
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            using (currentSchema.Change(_schema))
            {
                await using var uow = uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
                await store.StoreAsync(Envelope(subject: "instance-b"));
                await uow.RollbackAsync();
            }
        }

        publisher.Published.ShouldBeEmpty();
    }
```

`Envelope(...)` yardımcısı:

```csharp
    private static CloudEventEnvelope Envelope(string subject) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = "TestEvent",
        Topic = "test-topic",
        Subject = subject,
        Data = System.Text.Encoding.UTF8.GetBytes("{}")
    };
```

`BuildProvider(IOutboxWakeupPublisher publisher)` imzası, `NpgsqlLeaseStoreTests`'teki
sürüme şu iki satırı ekler:

```csharp
        services.AddSingleton(publisher);
        services.AddAetherOutbox<TestDbContext>(options =>
        {
            options.Schema = _schema;
            options.SignalEnabled = true;
        });
```

`AddSingleton(publisher)` çağrısı `AddAetherOutbox`'tan **önce** gelmeli — o metot
`TryAddSingleton` ile null publisher'ı kaydediyor, önce gerçek olan girerse ezilmez.

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~OutboxSignalIntegrationTests"
```

Beklenen: ilk test FAIL — `Published.Count` 1 yerine **0** (henüz kimse `Mark` çağırmıyor).

- [ ] **Step 3: Store'a collector'ı ekle**

`EfCoreOutboxStore`'un birincil kurucusuna `IOutboxSignalCollector signalCollector` ekle,
`ICurrentSchema? currentSchema` parametresinden **önce** (nullable olan sonda kalsın):

```csharp
public class EfCoreOutboxStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IEventSerializer eventSerializer,
    IGuidGenerator guidGenerator,
    IClock clock,
    AetherOutboxOptions options,
    IOutboxSignalCollector signalCollector,
    ICurrentSchema? currentSchema) : IOutboxStore
```

Geriye dönük uyumlu ikinci kurucu (`Schema = null` ile ambient davranışı koruyan) da bir
collector geçmek zorunda. Orada sinyal üretmek anlamsız — o kurucu zaten opsiyonsuz eski
davranış için var. `IOutboxSignalCollector`'ın no-op bir uygulamasını ekle:

`framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxSignalCollector.cs`:

```csharp
namespace BBT.Aether.Events;

/// <summary>
/// Collector used by the backward-compatible store constructor, which predates signalling.
/// Marking is a no-op; fallback polling still dispatches the rows.
/// </summary>
public sealed class NullOutboxSignalCollector : IOutboxSignalCollector
{
    public void Mark(string schema, short partitionId) { }
}
```

ve ikinci kurucuda `new NullOutboxSignalCollector()` geç.

`StoreAsync` içinde, `dbContext.OutboxMessages.AddAsync(...)` çağrısından **sonra** ekle:

```csharp
        signalCollector.Mark(
            options.Schema ?? currentSchema?.Name ?? string.Empty,
            outboxMessage.PartitionId);
```

Şema çözümü `BeginConfiguredSchemaScope()` ile aynı önceliği izlemeli — önce
`options.Schema`, yoksa ambient. Boş string'e düşerse sinyal yine gönderilir ama worker
eşleştiremez; bunu bir uyarı log'u ile görünür kıl.

- [ ] **Step 4: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~OutboxSignalIntegrationTests"
dotnet test framework/test/BBT.Aether.Postgres.Tests
```

Beklenen: yeni 2 test PASS; mevcut Postgres suite'i (152) regresyonsuz.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxSignalCollector.cs \
        framework/test/BBT.Aether.Postgres.Tests/OutboxSignalIntegrationTests.cs
git commit -m "feat(outbox): mark a wake-up signal when a row is written

Verified end to end against a real database: two rows for the same subject
coalesce to one signal, and a rollback publishes none."
```

---

## Task 5: Worker-side coordinator

**Neden:** Aynı partition için 10.000 sinyal gelse bile **tek** efektif kontrol olmalı.
Coordinator, sinyalleri bir pending set'te biriktirir ve uyuyan worker'ı uyandırır.

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/IOutboxSignalCoordinator.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/OutboxSignalCoordinator.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxSignalCoordinatorTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events.Processing;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxSignalCoordinatorTests
{
    [Fact]
    public async Task WaitAsync_returns_immediately_when_a_signal_is_already_pending()
    {
        var coordinator = new OutboxSignalCoordinator();
        coordinator.Signal("sys_queues", 3);

        var sw = Stopwatch.StartNew();
        var keys = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        sw.Stop();

        keys.Count.ShouldBe(1);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_returns_empty_after_the_fallback_timeout()
    {
        var coordinator = new OutboxSignalCoordinator();

        var keys = await coordinator.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);

        keys.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signal_arriving_while_waiting_wakes_the_waiter()
    {
        var coordinator = new OutboxSignalCoordinator();

        var waiting = coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);
        coordinator.Signal("sys_queues", 9);

        var keys = await waiting;

        keys.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ten_thousand_signals_for_one_partition_collapse_to_one_key()
    {
        var coordinator = new OutboxSignalCoordinator();
        for (var i = 0; i < 10_000; i++) coordinator.Signal("sys_queues", 2);

        var keys = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        keys.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Pending_keys_are_drained_and_not_returned_twice()
    {
        var coordinator = new OutboxSignalCoordinator();
        coordinator.Signal("sys_queues", 1);

        var first = await coordinator.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var second = await coordinator.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);

        first.Count.ShouldBe(1);
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task WaitAsync_honours_the_caller_cancellation_token()
    {
        var coordinator = new OutboxSignalCoordinator();
        using var cts = new CancellationTokenSource();
        var waiting = coordinator.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        await Task.Delay(50);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
    }
}
```

- [ ] **Step 2: Testi çalıştır, derlenmediğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxSignalCoordinatorTests"
```

Beklenen: `CS0246 ... 'OutboxSignalCoordinator' could not be found`.

- [ ] **Step 3: Sözleşmeyi oluştur**

`IOutboxSignalCoordinator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events.Processing;

/// <summary>Signal key: which outbox table and partition has work.</summary>
public readonly record struct OutboxSignalKey(string Schema, short PartitionId);

/// <summary>
/// Bridges incoming wake-up signals to the dispatcher loop.
/// </summary>
/// <remarks>
/// Singleton. Signalling is fire-and-forget and must never block the caller — the subscription
/// endpoint has to return promptly so the broker does not tie its retry behaviour to dispatch
/// processing.
/// </remarks>
public interface IOutboxSignalCoordinator
{
    /// <summary>Records a pending signal and wakes a waiting dispatcher, if any.</summary>
    void Signal(string schema, short partitionId);

    /// <summary>
    /// Waits for at least one signal or until <paramref name="timeout"/> elapses, then drains
    /// and returns the pending keys. An empty result means the fallback timeout fired.
    /// </summary>
    Task<IReadOnlyCollection<OutboxSignalKey>> WaitAsync(
        TimeSpan timeout, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Coordinator'ı oluştur**

`OutboxSignalCoordinator.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// In-memory coordinator coalescing wake-up signals for the dispatcher loop.
/// </summary>
public sealed class OutboxSignalCoordinator : IOutboxSignalCoordinator
{
    private readonly ConcurrentDictionary<OutboxSignalKey, byte> _pending = new();

    // Capacity 1 with DropWrite: the channel is only a doorbell. Extra rings while one is
    // already pending are redundant — the pending set carries the actual information.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public void Signal(string schema, short partitionId)
    {
        _pending.TryAdd(new OutboxSignalKey(schema, partitionId), 0);
        _wake.Writer.TryWrite(true);
    }

    public async Task<IReadOnlyCollection<OutboxSignalKey>> WaitAsync(
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await _wake.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fallback timeout — expected, not an error.
            }
        }

        // Collapse any extra doorbell rings.
        while (_wake.Reader.TryRead(out _)) { }

        var keys = _pending.Keys.ToArray();
        foreach (var key in keys) _pending.TryRemove(key, out _);
        return keys;
    }
}
```

> Drain ile yeni sinyal arasında bir yarış var: bir key drain edildikten hemen sonra aynı
> partition'a yazan bir transaction yeni bir sinyal koyar ve bir sonraki turda tekrar
> kontrol edilir. Bu yalnızca fazladan bir kontrol üretir, veri kaybı değil.

- [ ] **Step 5: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxSignalCoordinatorTests"
```

Beklenen: 6 test PASS.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/IOutboxSignalCoordinator.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/OutboxSignalCoordinator.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxSignalCoordinatorTests.cs
git commit -m "feat(outbox): add in-memory wake-up signal coordinator

Ten thousand signals for one partition collapse to a single effective check."
```

---

## Task 6: Dispatcher döngüsünü sinyale bağla

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs`

- [ ] **Step 1: `Task.Delay`'i coordinator ile değiştir**

Kurucuya `IOutboxSignalCoordinator signalCoordinator` ekle. `ExecuteAsync`'in sonundaki:

```csharp
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
```

satırını şununla değiştir:

```csharp
            // Sleep until a wake-up signal arrives or the fallback interval elapses, whichever
            // comes first. Fallback polling is the correctness mechanism and stays active even
            // when signalling is healthy — a lost signal must only cost latency, never data.
            await signalCoordinator.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
```

Dönen key koleksiyonu Faz 2'de **kullanılmıyor** — hangi partition sinyallendiği Faz 3'ün
partition'lı lease'inde anlam kazanacak. Bu yüzden sonucu bilinçli olarak yok sayıyoruz;
bunu bir yorumla belirt ki gelecekte "ölü kod" sanılmasın.

- [ ] **Step 2: Derle ve mevcut testleri çalıştır**

```bash
dotnet build
dotnet test framework/test/BBT.Aether.Infrastructure.Tests
```

Beklenen: 0 error. `AdaptivePollingTests` etkilenmemeli (saf fonksiyon, dokunulmadı).

`OutboxBackgroundService`'i doğrudan test eden bir şey varsa kurucu değişikliğinden
etkilenir — kırılırsa raporla, testi zayıflatma.

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs
git commit -m "feat(outbox): wake the dispatcher on a signal instead of sleeping a fixed delay

The fallback interval becomes an upper bound rather than the normal latency."
```

---

## Task 7: DI kayıtları

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherOutboxServiceCollectionExtensions.cs`

- [ ] **Step 1: Kayıtları ekle**

`AddAetherOutbox<TDbContext>` içinde, mevcut `services.AddScoped<IOutboxStore, ...>()`
satırının yanına:

```csharp
        // Scoped: one collector per unit of work, so coalescing is per transaction.
        services.AddScoped<IOutboxSignalCollector, OutboxSignalCollector>();

        // Null fallback — a Dapr-backed publisher is registered by the hosting application.
        services.TryAddSingleton<IOutboxWakeupPublisher, NullOutboxWakeupPublisher>();

        // Singleton: the dispatcher loop and the subscription endpoint share one instance.
        services.TryAddSingleton<IOutboxSignalCoordinator, OutboxSignalCoordinator>();
```

`TryAdd*` kullanımı önemli: uygulama kendi publisher'ını daha önce kaydettiyse ezilmesin.

- [ ] **Step 2: Dapr publisher'ı kaydetmek için bir extension ekle**

Aynı dosyaya, `AddAetherOutbox`'ın altına:

```csharp
    /// <summary>
    /// Registers the Dapr-backed wake-up signal publisher, replacing the no-op default.
    /// Call from an application that already has a <see cref="Dapr.Client.DaprClient"/> registered.
    /// </summary>
    public static IServiceCollection AddAetherOutboxDaprSignalling(this IServiceCollection services)
    {
        services.RemoveAll<IOutboxWakeupPublisher>();
        services.AddSingleton<IOutboxWakeupPublisher, DaprOutboxWakeupPublisher>();
        return services;
    }
```

`RemoveAll` için `Microsoft.Extensions.DependencyInjection.Extensions` using'i gerekiyor.

- [ ] **Step 3: Derle ve tüm testleri çalıştır**

```bash
dotnet build
dotnet test
```

Beklenen: 0 error; Infrastructure 175 + yeni testler, Postgres 152 + yeni testler, SqlServer 2.

- [ ] **Step 4: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherOutboxServiceCollectionExtensions.cs
git commit -m "feat(outbox): register signal collector, coordinator and publisher"
```

---

## Task 8: vNext subscription endpoint

**Working directory:** `/Users/U0B006/Documents/repos/burgan-tech/vnext`, branch `feature/outbox-faz2-signal`.

**Files:**
- Create: `workers/BBT.Workflow.Workers.Outbox/Controllers/OutboxWakeupController.cs`

Outbox worker'da `UseCloudEvents()`, `MapSubscribeHandler()` ve `MapControllers()` **zaten
var** (`OutboxWorkerApplicationBuilderExtensions.cs`), yani pipeline değişikliği gerekmiyor.

- [ ] **Step 1: Controller'ı oluştur**

```csharp
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Outbox.Controllers;

/// <summary>
/// Receives outbox wake-up signals from Dapr pub/sub.
/// </summary>
/// <remarks>
/// Does nothing but nudge the in-memory coordinator and return. No database query, no publish,
/// no retry loop: the endpoint must return promptly so the broker's retry behaviour is not
/// coupled to dispatch processing. A signal is a hint — dropping one only costs latency.
/// </remarks>
[ApiController]
public sealed class OutboxWakeupController(
    IOutboxSignalCoordinator coordinator,
    AetherOutboxOptions outboxOptions,
    ILogger<OutboxWakeupController> logger) : ControllerBase
{
    /// <summary>Handles a wake-up signal. Always returns 200 so the broker does not redeliver.</summary>
    [HttpPost("/internal/outbox/wakeup")]
    public IActionResult Wakeup([FromBody] OutboxWakeupSignal signal)
    {
        if (signal is null)
        {
            logger.LogWarning("Outbox wake-up signal had no body; ignoring.");
            return Ok();
        }

        if (signal.PartitionId < OutboxWakeupSignal.AllPartitions ||
            signal.PartitionId >= outboxOptions.PartitionCount)
        {
            logger.LogWarning(
                "Outbox wake-up signal had out-of-range PartitionId {PartitionId}; ignoring.",
                signal.PartitionId);
            return Ok();
        }

        if (!string.Equals(signal.Schema, outboxOptions.Schema, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Outbox wake-up signal for schema {SignalSchema} ignored; this worker serves {WorkerSchema}.",
                signal.Schema, outboxOptions.Schema);
            return Ok();
        }

        coordinator.Signal(signal.Schema, signal.PartitionId);
        return Ok();
    }
}
```

**Neden hep `Ok()`:** Bir sinyal yalnızca ipucudur. Geçersiz bir sinyale hata döndürmek
broker'ı yeniden teslime zorlar ve hiçbir şey kazandırmaz — kayıtlar fallback poll ile
zaten bulunur. Geçersizlik log'a yazılır, sessizce yutulmaz.

- [ ] **Step 2: Derle**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet build BBT.Workflow.slnx
```

Beklenen: 0 error. `AetherOutboxOptions`'ın DI'dan çözülebildiğini doğrula — `AddAetherOutbox`
onu singleton kaydediyor.

- [ ] **Step 3: Commit**

```bash
git add workers/BBT.Workflow.Workers.Outbox/Controllers/OutboxWakeupController.cs
git commit -m "feat(outbox-worker): accept Dapr wake-up signals

Validates schema and partition range, then only nudges the coordinator. Always
returns 200: a rejected hint would trigger pointless broker redelivery."
```

---

## Task 9: vNext Dapr subscription ve konfigürasyon

**Files:**
- Create: `etc/workers/outbox/dapr/components/outbox-wakeup-subscription.yaml`
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json`
- Modify: `workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs`

- [ ] **Step 1: Subscription YAML'ini oluştur**

Mevcut `etc/workers/inbox/dapr/components/definition-component-published-subscription.yaml`
desenini izle:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Subscription
metadata:
  name: vnext-outbox-wakeup-subscription
spec:
  topic: outbox-wakeup
  route: /internal/outbox/wakeup
  pubsubname: vnext-pubsub
```

**`vnext-pubsub` kullanılıyor, `vnext-pubsub-broadcast` DEĞİL.** Bu, tasarımın dayandığı
noktadır: competing-consumer semantiği sayesinde bir sinyal app-id'nin **tek** replica'sına
gider, dolayısıyla partition ownership'i koordinasyonsuz elde ederiz. Broadcast component'i
kullanılırsa her sinyal 10 pod'a gider ve tasarım bozulur.

> ⚠️ `consumerID` repoda hiçbir yerde set edilmemiş, Dapr varsayılan olarak app-id kullanır.
> **Preprod/prod deploy konfigürasyonundan pod-başına unique OLMADIĞI doğrulanmalı** — unique
> ise her sinyal tüm pod'lara dağılır.

- [ ] **Step 2: Dapr publisher'ı worker'da kaydet**

`OutboxWorkerServiceCollectionExtensions.AddOutboxMessagingContext` içinde,
`services.AddAetherOutbox<MessagingDbContext>(...)` çağrısından hemen sonra:

```csharp
        // Replaces the SDK's no-op publisher; the worker already has a DaprClient.
        services.AddAetherOutboxDaprSignalling();
```

- [ ] **Step 3: appsettings'i güncelle**

`Aether:Outbox` bloğuna ekle:

```json
      "SignalEnabled": true,
      "SignalTopic": "outbox-wakeup",
      "SignalTtlSeconds": 30,
```

`MaxPollingInterval` **şimdilik `00:01:00` kalsın.** 300 saniyeye çıkarmak T10'un
doğrulamasından sonra, ayrı bir adımda yapılacak — sinyalin gerçekten çalıştığı
kanıtlanmadan fallback'i gevşetmek, sinyal bozuksa latency'yi 5 dakikaya çıkarır.

- [ ] **Step 4: JSON'u doğrula ve derle**

```bash
python3 -m json.tool workers/BBT.Workflow.Workers.Outbox/appsettings.json > /dev/null && echo "JSON ok"
dotnet build BBT.Workflow.slnx
```

Anahtar adlarını `AetherOutboxOptions`'daki gerçek property adlarıyla karşılaştır —
`Bind` yanlış anahtarda sessizce default'a düşer.

- [ ] **Step 5: Commit**

```bash
git add etc/workers/outbox/dapr/components/outbox-wakeup-subscription.yaml \
        workers/BBT.Workflow.Workers.Outbox/appsettings.json \
        workers/BBT.Workflow.Workers.Outbox/Microsoft/Extensions/DependencyInjection/OutboxWorkerServiceCollectionExtensions.cs
git commit -m "feat(outbox-worker): subscribe to wake-up signals over the competing-consumer pubsub

Uses vnext-pubsub, not the broadcast component: competing-consumer delivery is
what gives partition ownership without any coordination."
```

---

## Task 10: Uçtan uca doğrulama

Bu bir **doğrulama** görevi — production kodu değiştirmesi beklenmiyor. Bir kusur
bulursan raporla, tek başına düzeltme.

- [ ] **Step 1: Altyapıyı kaldır**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext/etc/docker && ./run-docker.sh
```

- [ ] **Step 2: Migration'ı uygula**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
APP_VERSION=1.0.0 APP_DOMAIN=core dotnet run --project workers/BBT.Workflow.DbMigrator
```

DbMigrator'ın distributed lock'u Dapr sidecar istiyor; `docker-compose.dev.yml`'den
`vnext-db-migrator-dapr` gerekebilir.

- [ ] **Step 3: Outbox worker'ı başlat ve sinyal yolunu gözlemle**

```bash
APP_VERSION=1.0.0 APP_DOMAIN=core dotnet run --project workers/BBT.Workflow.Workers.Outbox
```

Şunları doğrula ve raporla:
- worker hatasız açılıyor
- Dapr sidecar subscription'ı kaydediyor (`/dapr/subscribe` yanıtında ya da sidecar
  log'unda `outbox-wakeup` görünmeli)

- [ ] **Step 4: Sinyali elle tetikle**

Dapr sidecar üzerinden doğrudan publish et:

```bash
curl -s -X POST http://localhost:3500/v1.0/publish/vnext-pubsub/outbox-wakeup \
  -H 'Content-Type: application/json' \
  -d '{"schema":"sys_queues","partitionId":0,"source":"manual","emittedAt":"2026-07-28T12:00:00Z"}' \
  -w '\nHTTP %{http_code}\n'
```

Sidecar portunu çalışan yapılandırmadan doğrula. Worker log'unda döngünün uyandığını gör
(bir sonraki lease denemesi fallback aralığını beklemeden gerçekleşmeli).

Geçersiz sinyalleri de dene ve **200 döndüğünü ve log'landığını** doğrula:
- `"partitionId": 999` (aralık dışı)
- `"schema": "baska_schema"` (bu worker'a ait değil)

- [ ] **Step 5: Latency kazancını ölç**

Kaba ama yeterli bir ölçüm: `MaxPollingInterval` 60 sn iken, boşta bekleyen bir worker'a
sinyal gönder ve lease denemesine kadar geçen süreyi log zaman damgalarından çıkar.
Beklenen: **saniye altı**, 60 saniye değil.

Sinyali kapatıp (`SignalEnabled: false`) aynı ölçümü tekrarla — fallback aralığına düşmeli.
Bu, sinyalin gerçekten iş yaptığının kanıtıdır; yalnızca "hata vermedi" yeterli değil.

- [ ] **Step 6: Bulguları raporla**

Raporla: worker açılış çıktısı, subscription kaydı, elle publish'in HTTP kodu ve worker
log'undaki etkisi, geçersiz sinyal davranışı, sinyalli/sinyalsiz latency ölçümü.

Bir sonraki adım (bu planın **dışında**): ölçüm sinyalin çalıştığını gösterirse
`MaxPollingInterval`'ı 300 sn'ye çıkar ve outbox/inbox replica sayısını 10 → 2-3'e indir.
Spec §2.8'e göre asıl DB kazancı oradan geliyor.

---

## Bu planın kapsamı dışında

| Konu | Nerede |
|---|---|
| Partition okuma yolu (lease'e `PartitionId = ANY(...)`, sinyalin partition'ını kullanma) | **Faz 3** — coordinator'ın döndürdüğü key'ler o zaman anlam kazanacak |
| `MaxPollingInterval` 60 → 300 sn ve replica 10 → 2-3 | T10 ölçümü sinyali doğruladıktan sonra, ayrı adım |
| Inbox tarafına sinyal | Inbox'ı uyandıran zaten Dapr teslimi; boş poll sorunu outbox kadar keskin değil |
| Drasi'nin sinyal üreticisi olması | Spec §25-26 — kontrat zaten producer-agnostic, şimdi iş yok |
| Npgsql `Max Pool Size`, PgBouncer tuning, API pod churn | Spec §10 — bu plan pool sorununu çözmez |
| vNext'teki local-link commit'inin revert'i | PR öncesi, `git revert a7797dae` |
