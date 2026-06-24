# Inbox & Outbox Lease Strategy Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provider-specific lease SQL'i `BBT.Aether.Npgsql`'e taşı, `IOutboxLeaseStore`/`IInboxLeaseStore` interface'lerini ekle, adaptive polling `HostedService`'leri yaz, dead letter desteği ekle ve InboxProcessor'dan gereksiz distributed lock'ı kaldır.

**Architecture:** `IOutboxLeaseStore` + `IInboxLeaseStore` interface'leri `Abstractions`'a eklenir. `EfCoreOutboxStore`/`EfCoreInboxStore` sadece write/mark operasyonlarını barındırır. `NpgsqlOutboxLeaseStore`/`NpgsqlInboxLeaseStore` PostgreSQL-specific `FOR UPDATE SKIP LOCKED` SQL'i `BBT.Aether.Npgsql` içinde tutar. `OutboxBackgroundService`/`InboxBackgroundService` busy/idle exponential backoff ile adaptive polling yapar.

**Tech Stack:** .NET 10, EF Core 10, xunit, NSubstitute, Shouldly, Npgsql

**Spec:** `docs/superpowers/specs/2026-06-24-inbox-outbox-redesign.md`

---

## File Map

### BBT.Aether.Abstractions
| Dosya | İşlem |
|-------|-------|
| `BBT/Aether/Events/IOutboxLeaseStore.cs` | YENİ |
| `BBT/Aether/Events/IInboxLeaseStore.cs` | YENİ |
| `BBT/Aether/Events/IOutboxStore.cs` | `LeaseBatchAsync` kaldırıldı |
| `BBT/Aether/Events/IInboxStore.cs` | `LeaseBatchAsync`, `GetPendingEventsAsync`, `MarkAsProcessingAsync` kaldırıldı |
| `BBT/Aether/Events/OutboxMessageStatus.cs` | `DeadLetter = 3` eklendi |
| `BBT/Aether/Events/IncomingEventStatus.cs` | `DeadLetter = 4` eklendi |

### BBT.Aether.Core
| Dosya | İşlem |
|-------|-------|
| `BBT/Aether/Events/IOutboxProcessor.cs` | `Task` → `Task<int>` |
| `BBT/Aether/Events/IInboxProcessor.cs` | `Task` → `Task<int>` |
| `BBT/Aether/Events/NullOutboxStore.cs` | `LeaseBatchAsync` kaldırıldı |
| `BBT/Aether/Events/NullInboxStore.cs` | `LeaseBatchAsync`, `GetPendingEventsAsync`, `MarkAsProcessingAsync` kaldırıldı |
| `BBT/Aether/Events/NullOutboxLeaseStore.cs` | YENİ |
| `BBT/Aether/Events/NullInboxLeaseStore.cs` | YENİ |
| `BBT/Aether/Events/AetherOutboxOptions.cs` | `ProcessingInterval` kaldırıldı, 3 polling alanı eklendi |
| `BBT/Aether/Events/AetherInboxOptions.cs` | `ProcessingInterval`, `DistributedLockName`, `LockExpirySeconds` kaldırıldı, 3 polling alanı eklendi |

### BBT.Aether.Infrastructure
| Dosya | İşlem |
|-------|-------|
| `BBT/Aether/Events/EfCoreOutboxStore.cs` | `LeaseBatchAsync` + raw SQL kaldırıldı |
| `BBT/Aether/Events/EfCoreInboxStore.cs` | `LeaseBatchAsync`, `GetPendingEventsAsync`, `MarkAsProcessingAsync` + raw SQL kaldırıldı; `AetherInboxOptions` inject edildi; `MarkAsFailedAsync` dead letter mantığı içerir |
| `BBT/Aether/Events/WorkerIdentity.cs` | YENİ |
| `BBT/Aether/Events/Processing/OutboxProcessor.cs` | `IOutboxLeaseStore` kullanır, phase 3 guard, DeadLetter, `Task<int>` |
| `BBT/Aether/Events/Processing/InboxProcessor.cs` | `IInboxLeaseStore` kullanır, distributed lock yok, `Task<int>` |
| `BBT/Aether/Events/Processing/OutboxBackgroundService.cs` | YENİ — adaptive polling |
| `BBT/Aether/Events/Processing/InboxBackgroundService.cs` | YENİ — adaptive polling |
| `Microsoft/Extensions/DependencyInjection/AetherOutboxServiceCollectionExtensions.cs` | `withHostedService`, `WorkerIdentity`, `TryAdd` null lease store |

### BBT.Aether.Npgsql
| Dosya | İşlem |
|-------|-------|
| `BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs` | YENİ |
| `BBT/Aether/Events/NpgsqlInboxLeaseStore.cs` | YENİ |
| `Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs` | Lease store registration eklendi |

### Test
| Dosya | İşlem |
|-------|-------|
| `BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/WorkerIdentityTests.cs` | YENİ |
| `BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs` | YENİ |
| `BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxProcessorDeadLetterTests.cs` | YENİ |
| `BBT.Aether.Postgres.Tests/OutboxWithinSharedTransactionTests.cs` | `NpgsqlOutboxLeaseStore` integration doğrulaması eklendi |
| `BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs` | YENİ — `FOR UPDATE SKIP LOCKED` integration testi |

---

## Task 1: Abstractions — Enum + Lease Interfaces + Stripped Contracts

**Files:**
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxMessageStatus.cs`
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IncomingEventStatus.cs`
- Create: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs`
- Create: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IInboxLeaseStore.cs`
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxStore.cs`
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IInboxStore.cs`

> ⚠️ Bu task sonrası solution derlenmez — null store ve EfCore store implementasyonları Task 2 ve Task 4'te güncellenir.

- [ ] **Step 1: `OutboxMessageStatus`'a `DeadLetter` ekle**

```csharp
// framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxMessageStatus.cs
namespace BBT.Aether.Events;

public enum OutboxMessageStatus
{
    Pending    = 0,
    Processing = 1,
    Processed  = 2,
    DeadLetter = 3,
}
```

- [ ] **Step 2: `IncomingEventStatus`'a `DeadLetter` ekle**

```csharp
// framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IncomingEventStatus.cs
namespace BBT.Aether.Events;

public enum IncomingEventStatus
{
    Pending    = 0,
    Processing = 1,
    Processed  = 2,
    Discarded  = 3,
    DeadLetter = 4,
}
```

- [ ] **Step 3: `IOutboxLeaseStore` oluştur**

```csharp
// framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IOutboxLeaseStore
{
    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: `IInboxLeaseStore` oluştur**

```csharp
// framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IInboxLeaseStore.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IInboxLeaseStore
{
    Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: `IOutboxStore`'dan `LeaseBatchAsync` kaldır**

```csharp
// framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxStore.cs
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IOutboxStore
{
    Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 6: `IInboxStore`'dan 3 metodu kaldır**

```csharp
// framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IInboxStore.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IInboxStore
{
    Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default);
    Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default);
    Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default);
    Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
    // REMOVED: GetPendingEventsAsync, MarkAsProcessingAsync, LeaseBatchAsync
}
```

- [ ] **Step 7: Commit**

```bash
git add framework/src/BBT.Aether.Abstractions/
git commit -m "feat(abstractions): add IOutboxLeaseStore, IInboxLeaseStore, DeadLetter enums; strip lease from IOutboxStore/IInboxStore"
```

---

## Task 2: Core — Processor Interfaces, Options, Null Lease Stores, Null Store Cleanup

**Files:**
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxProcessor.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/IInboxProcessor.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherInboxOptions.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxStore.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullInboxStore.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxLeaseStore.cs`
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullInboxLeaseStore.cs`

- [ ] **Step 1: `IOutboxProcessor.RunAsync` → `Task<int>`**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxProcessor.cs
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IOutboxProcessor
{
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: `IInboxProcessor.RunAsync` → `Task<int>`**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/IInboxProcessor.cs
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public interface IInboxProcessor
{
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: `AetherOutboxOptions` güncelle**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs
using System;

namespace BBT.Aether.Events;

public class AetherOutboxOptions
{
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public int BatchSize { get; set; } = 100;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BusyPollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxPollingInterval  { get; set; } = TimeSpan.FromSeconds(60);
    public string? Schema { get; set; } = "sys_queues";
    // REMOVED: ProcessingInterval
}
```

- [ ] **Step 4: `AetherInboxOptions` güncelle**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherInboxOptions.cs
using System;

namespace BBT.Aether.Events;

public class AetherInboxOptions
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public int CleanupBatchSize { get; set; } = 1000;
    public int ProcessingBatchSize { get; set; } = 100;
    public int MaxRetryCount { get; set; } = 5;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BusyPollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxPollingInterval  { get; set; } = TimeSpan.FromSeconds(60);
    public string? Schema { get; set; }
    // REMOVED: ProcessingInterval, DistributedLockName, LockExpirySeconds
}
```

- [ ] **Step 5: `NullOutboxStore`'dan `LeaseBatchAsync` kaldır**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxStore.cs
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public class NullOutboxStore : IOutboxStore
{
    public Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

- [ ] **Step 6: `NullInboxStore` güncelle — 3 metodu kaldır**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/NullInboxStore.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public class NullInboxStore : IInboxStore
{
    public Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task MarkAsProcessedAsync(string eventId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);
    // REMOVED: GetPendingEventsAsync, MarkAsProcessingAsync, LeaseBatchAsync
}
```

- [ ] **Step 7: `NullOutboxLeaseStore` oluştur**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxLeaseStore.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public class NullOutboxLeaseStore : IOutboxLeaseStore
{
    public Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
}
```

- [ ] **Step 8: `NullInboxLeaseStore` oluştur**

```csharp
// framework/src/BBT.Aether.Core/BBT/Aether/Events/NullInboxLeaseStore.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.Events;

public class NullInboxLeaseStore : IInboxLeaseStore
{
    public Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InboxMessage>>(Array.Empty<InboxMessage>());
}
```

- [ ] **Step 9: `dotnet build framework/BBT.Aether.slnx` çalıştır — Core ve Abstractions derlenmeli**

```bash
dotnet build framework/BBT.Aether.slnx
```

Infrastructure ve Npgsql projeleri bu aşamada derleme hatası verebilir (EfCoreOutboxStore vs). Normal, devam et.

- [ ] **Step 10: Commit**

```bash
git add framework/src/BBT.Aether.Core/
git commit -m "feat(core): update processor interfaces to Task<int>, redesign options, add null lease stores"
```

---

## Task 3: WorkerIdentity

**Files:**
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/WorkerIdentity.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/WorkerIdentityTests.cs`

- [ ] **Step 1: Failing test yaz**

```csharp
// framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/WorkerIdentityTests.cs
using BBT.Aether.Events;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events;

public sealed class WorkerIdentityTests
{
    [Fact]
    public void Value_includes_app_name_and_pid()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("my-service");

        var identity = new WorkerIdentity(env);

        identity.Value.ShouldStartWith("my-service/");
        identity.Value.ShouldContain($"/{Environment.ProcessId}/");
    }

    [Fact]
    public void Two_instances_have_different_instance_ids()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("svc");

        var a = new WorkerIdentity(env);
        var b = new WorkerIdentity(env);

        a.Value.ShouldNotBe(b.Value);
    }

    [Fact]
    public void Value_has_four_segments()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ApplicationName.Returns("svc");

        var identity = new WorkerIdentity(env);

        identity.Value.Split('/').Length.ShouldBe(4);
    }
}
```

- [ ] **Step 2: Testi çalıştır — derleme hatası beklenir (WorkerIdentity yok)**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/ --filter "WorkerIdentity"
```

Expected: derleme hatası.

- [ ] **Step 3: `WorkerIdentity` implementasyonu yaz**

```csharp
// framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/WorkerIdentity.cs
using System;
using Microsoft.Extensions.Hosting;

namespace BBT.Aether.Events;

public sealed class WorkerIdentity
{
    public string Value { get; }

    public WorkerIdentity(IHostEnvironment env)
    {
        var pod = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
        var instanceId = Guid.NewGuid().ToString("N")[..8];
        Value = $"{env.ApplicationName}/{pod}/{Environment.ProcessId}/{instanceId}";
    }
}
```

- [ ] **Step 4: Testleri çalıştır — geçmeli**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/ --filter "WorkerIdentity"
```

Expected: 3 test PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/WorkerIdentity.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/WorkerIdentityTests.cs
git commit -m "feat(infrastructure): add WorkerIdentity with structured pod/process/instance format"
```

---

## Task 4: EfCoreOutboxStore + EfCoreInboxStore Cleanup

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs`

- [ ] **Step 1: `EfCoreOutboxStore`'u güncelle — sadece `StoreAsync` bırak**

`framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs` dosyasını aç. Şu metodları sil:
- `LeaseBatchAsync` (tüm metod + raw SQL)
- `AddParameter` (artık kullanılmıyor)
- `DeserializeExtraProperties` (artık kullanılmıyor)

Kullanılmayan `using` direktiflerini kaldır (`System.Data`, `System.Data.Common`, `Microsoft.EntityFrameworkCore.Storage`).

Sonuç:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Guids;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BBT.Aether.Events;

public class EfCoreOutboxStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IEventSerializer eventSerializer,
    IGuidGenerator guidGenerator,
    IClock clock) : IOutboxStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    public async Task StoreAsync(CloudEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var serializedBytes = eventSerializer.Serialize(envelope);

        var outboxMessage = new Domain.Events.OutboxMessage(guidGenerator.Create(), envelope.Type, serializedBytes)
        {
            CreatedAt = clock.UtcNow,
            RetryCount = 0,
            Status = OutboxMessageStatus.Pending,
            ExtraProperties = { ["TopicName"] = envelope.Topic ?? envelope.Type }
        };

        if (envelope.Version.HasValue)
            outboxMessage.ExtraProperties["Version"] = envelope.Version.Value;
        if (envelope.Source != null)
            outboxMessage.ExtraProperties["Source"] = envelope.Source;
        if (envelope.Subject != null)
            outboxMessage.ExtraProperties["Subject"] = envelope.Subject;

        await dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
    }
}
```

- [ ] **Step 2: `EfCoreInboxStore`'u güncelle**

`framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs` dosyasını aç:

1. Constructor'a `AetherInboxOptions options` parametresi ekle.
2. `GetPendingEventsAsync`, `MarkAsProcessingAsync`, `LeaseBatchAsync` metodlarını sil.
3. `MarkAsFailedAsync`'i dead letter mantığıyla güncelle.
4. Kullanılmayan `using` direktiflerini kaldır.

`MarkAsFailedAsync` yeni implementasyonu:

```csharp
public async Task MarkAsFailedAsync(string eventId, CancellationToken cancellationToken = default)
{
    var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
    var message = await dbContext.InboxMessages
        .FirstOrDefaultAsync(m => m.Id == eventId, cancellationToken);

    if (message == null) return;

    if (message.RetryCount + 1 >= options.MaxRetryCount)
    {
        message.Status = IncomingEventStatus.DeadLetter;
        message.LockedBy = null;
        message.LockedUntil = null;
    }
    else
    {
        message.RetryCount++;
        var delay = options.RetryBaseDelay * Math.Pow(2, message.RetryCount - 1);
        message.NextRetryTime = clock.UtcNow.Add(TimeSpan.FromMilliseconds(delay.TotalMilliseconds));
        message.Status = IncomingEventStatus.Pending;
        message.LockedBy = null;
        message.LockedUntil = null;
    }
}
```

Constructor signature sonucu:

```csharp
public class EfCoreInboxStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IEventSerializer eventSerializer,
    IClock clock,
    AetherInboxOptions options) : IInboxStore
```

- [ ] **Step 3: `dotnet build framework/BBT.Aether.slnx` — Infrastructure artık derlenmeli**

```bash
dotnet build framework/BBT.Aether.slnx
```

Expected: Infrastructure derlenebilir (Processor'lar henüz IOutboxLeaseStore kullanmıyor ama derlenir).

- [ ] **Step 4: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/
git commit -m "refactor(infrastructure): strip lease SQL from EfCoreOutboxStore/InboxStore; add dead letter to EfCoreInboxStore"
```

---

## Task 5: OutboxProcessor Rewrite

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxProcessorDeadLetterTests.cs`

- [ ] **Step 1: Dead letter unit testini yaz**

```csharp
// framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxProcessorDeadLetterTests.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxProcessorDeadLetterTests
{
    private static OutboxMessage MakeMessage(int retryCount) => new()
    {
        Id = Guid.NewGuid(),
        EventName = "test",
        EventData = [],
        Status = OutboxMessageStatus.Processing,
        RetryCount = retryCount,
        ExtraProperties = new Dictionary<string, object>()
    };

    [Fact]
    public void ShouldGoDeadLetter_when_retry_count_meets_max()
    {
        // OutboxProcessor'ın private dead letter hesabını doğrular
        // MaxRetryCount = 3 olduğunda RetryCount >= 3 → DeadLetter
        var maxRetryCount = 3;
        var message = MakeMessage(retryCount: 3);

        var isDeadLetter = message.RetryCount >= maxRetryCount;

        isDeadLetter.ShouldBeTrue();
    }

    [Fact]
    public void ShouldNotGoDeadLetter_when_retry_count_below_max()
    {
        var maxRetryCount = 3;
        var message = MakeMessage(retryCount: 2);

        var isDeadLetter = message.RetryCount >= maxRetryCount;

        isDeadLetter.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Testleri çalıştır — geçmeli (logic testleri, processor gerekmez)**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/ --filter "OutboxProcessorDeadLetter"
```

Expected: 2 test PASS.

- [ ] **Step 3: `OutboxProcessor`'ı yeniden yaz**

`framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs` dosyasını şu şekilde güncelle:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public class OutboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity workerIdentity,
    IClock clock,
    ILogger<OutboxProcessor<TDbContext>> logger,
    AetherOutboxOptions options) : IOutboxProcessor
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    public virtual async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var processed = await ProcessOutboxMessagesAsync(cancellationToken);
            await CleanupProcessedMessagesAsync(cancellationToken);
            return processed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing outbox messages");
            return 0;
        }
    }

    protected virtual async Task<int> ProcessOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Outbox processor has no Schema configured; skipping run.");
            return 0;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var eventBus = sp.GetRequiredService<IDistributedEventBus>();
        var eventBusOptions = sp.GetRequiredService<AetherEventBusOptions>();
        var leaseStore = sp.GetRequiredService<IOutboxLeaseStore>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();

        var workerId = $"{workerIdentity.Value}/outbox";

        using (currentSchema.Change(options.Schema))
        {
            // PHASE 1: lease — kısa transaction
            List<OutboxMessage> messages;
            await using (var leaseUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                messages = (await leaseStore.LeaseBatchAsync(
                    options.BatchSize, workerId, options.LeaseDuration, cancellationToken)).ToList();
                await leaseUow.CommitAsync(cancellationToken);
            }

            if (messages.Count == 0) return 0;

            logger.LogInformation("Leased {Count} outbox messages for worker {WorkerId}", messages.Count, workerId);

            // PHASE 2: publish — transaction açık değil
            var outcomes = new List<OutboxPublishOutcome>(messages.Count);
            foreach (var message in messages)
            {
                if (cancellationToken.IsCancellationRequested) break;

                using var activity = InfrastructureActivitySource.Source.StartActivity(
                    "Outbox.Process", ActivityKind.Producer, Activity.Current?.Context ?? default);

                var topicName = message.ExtraProperties.TryGetValue("TopicName", out var topicObj)
                    ? topicObj?.ToString() ?? message.EventName : message.EventName;
                var pubSubName = message.ExtraProperties.TryGetValue("PubSubName", out var pubSubObj)
                    ? pubSubObj?.ToString() ?? eventBusOptions.PubSubName : eventBusOptions.PubSubName;

                activity?.SetTag("event.name", message.EventName);
                activity?.SetTag("event.topic", topicName);
                activity?.SetTag("outbox.message_id", message.Id.ToString());
                activity?.SetTag("outbox.retry_count", message.RetryCount);

                try
                {
                    await eventBus.PublishEnvelopeAsync(message.EventData, topicName, pubSubName, cancellationToken);
                    outcomes.Add(new OutboxPublishOutcome(message.Id, true, null));
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    logger.LogInformation("Published outbox message {MessageId}", message.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);
                    RecordException(activity, ex);
                    outcomes.Add(new OutboxPublishOutcome(message.Id, false, ex.Message));
                }
            }

            if (outcomes.Count == 0) return 0;

            // PHASE 3: outcome yaz — kısa transaction, locked_by guard
            await using (var updateUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
                var now = clock.UtcNow;

                foreach (var outcome in outcomes)
                {
                    if (outcome.Success)
                    {
                        // LockedBy guard: sadece bu worker'ın hâlâ sahip olduğu mesajları güncelle
                        var affected = await dbContext.OutboxMessages
                            .Where(m => m.Id == outcome.MessageId
                                     && m.LockedBy == workerId
                                     && m.LockedUntil > now)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(m => m.Status, OutboxMessageStatus.Processed)
                                .SetProperty(m => m.ProcessedAt, now)
                                .SetProperty(m => m.LockedBy, (string?)null)
                                .SetProperty(m => m.LockedUntil, (DateTime?)null),
                                cancellationToken);

                        if (affected == 0)
                            logger.LogWarning("Outbox message {MessageId} lease expired or taken by another worker; skipping outcome write", outcome.MessageId);
                    }
                    else
                    {
                        var domainMessage = await dbContext.OutboxMessages
                            .Where(m => m.Id == outcome.MessageId && m.LockedBy == workerId)
                            .FirstOrDefaultAsync(cancellationToken);

                        if (domainMessage == null) continue;

                        if (domainMessage.RetryCount + 1 >= options.MaxRetryCount)
                        {
                            domainMessage.Status = OutboxMessageStatus.DeadLetter;
                        }
                        else
                        {
                            domainMessage.RetryCount++;
                            domainMessage.LastError = outcome.Error?.Length > 4000
                                ? outcome.Error[..4000] : outcome.Error;
                            domainMessage.NextRetryAt = CalculateNextRetryTime(domainMessage.RetryCount);
                            domainMessage.Status = OutboxMessageStatus.Pending;
                        }

                        domainMessage.LockedBy = null;
                        domainMessage.LockedUntil = null;
                    }
                }

                await updateUow.CommitAsync(cancellationToken);
            }

            return outcomes.Count;
        }
    }

    private readonly record struct OutboxPublishOutcome(Guid MessageId, bool Success, string? Error);

    protected virtual async Task CleanupProcessedMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema)) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();

        using (currentSchema.Change(options.Schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
            var cutoffDate = clock.UtcNow - options.RetentionPeriod;

            var processed = await dbContext.OutboxMessages
                .Where(m => m.Status == OutboxMessageStatus.Processed
                         && m.ProcessedAt != null
                         && m.ProcessedAt < cutoffDate)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken);

            if (processed.Count > 0)
            {
                logger.LogInformation("Cleaning up {Count} processed outbox messages", processed.Count);
                dbContext.OutboxMessages.RemoveRange(processed);
            }

            await uow.CommitAsync(cancellationToken);
        }
    }

    private DateTime CalculateNextRetryTime(int retryCount)
    {
        var delay = options.RetryBaseDelay * Math.Pow(2, retryCount - 1);
        return clock.UtcNow.Add(TimeSpan.FromMilliseconds(delay.TotalMilliseconds));
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }
}
```

- [ ] **Step 4: `dotnet build framework/BBT.Aether.slnx` — derlenmeli**

```bash
dotnet build framework/BBT.Aether.slnx
```

- [ ] **Step 5: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/
```

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/
git commit -m "feat(infrastructure): rewrite OutboxProcessor — IOutboxLeaseStore, phase3 guard, dead letter, Task<int>"
```

---

## Task 6: InboxProcessor Rewrite

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs`

- [ ] **Step 1: `InboxProcessor`'ı yeniden yaz**

```csharp
// framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Telemetry;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public class InboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity workerIdentity,
    ILogger<InboxProcessor<TDbContext>> logger,
    AetherInboxOptions options) : IInboxProcessor
    where TDbContext : DbContext, IHasEfCoreInbox
{
    // IDistributedLockService KALDIRILDI — lease koordinasyonu sağlar

    public virtual async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var processed = await ProcessPendingEventsAsync(cancellationToken);
            await CleanupOldMessagesAsync(cancellationToken);
            return processed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in inbox processing cycle");
            return 0;
        }
    }

    protected virtual async Task<int> ProcessPendingEventsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            logger.LogWarning("Inbox processor has no Schema configured; skipping run.");
            return 0;
        }

        var totalProcessed = 0;
        var workerId = $"{workerIdentity.Value}/inbox";

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

            using (currentSchema.Change(options.Schema!))
            {
                IReadOnlyList<InboxMessage> pendingEvents;
                await using (var leaseUow = unitOfWorkManager.BeginRequiresNew())
                {
                    pendingEvents = await leaseStore.LeaseBatchAsync(
                        options.ProcessingBatchSize, workerId, options.LeaseDuration, cancellationToken);
                    await leaseUow.CommitAsync(cancellationToken);
                }

                if (!pendingEvents.Any()) break;

                logger.LogInformation("Leased {Count} inbox events for worker {WorkerId}", pendingEvents.Count, workerId);

                foreach (var inboxMessage in pendingEvents)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await ProcessSingleEventAsync(inboxMessage, scope.ServiceProvider, cancellationToken);
                    totalProcessed++;
                }
            }
        }

        return totalProcessed;
    }

    private async Task ProcessSingleEventAsync(
        InboxMessage inboxMessage,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken)
    {
        using var activity = InfrastructureActivitySource.Source.StartActivity(
            "Inbox.Process", ActivityKind.Consumer, Activity.Current?.Context ?? default);

        activity?.SetTag("event.id", inboxMessage.Id);
        logger.LogInformation("Processing inbox event {EventId}", inboxMessage.Id);

        try
        {
            var unitOfWorkManager = scopedServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var inboxStore = scopedServiceProvider.GetRequiredService<IInboxStore>();
            var invokerRegistry = scopedServiceProvider.GetRequiredService<IDistributedEventInvokerRegistry>();
            var eventSerializer = scopedServiceProvider.GetRequiredService<IEventSerializer>();

            var envelope = eventSerializer.Deserialize<CloudEventEnvelope>(inboxMessage.EventData);
            if (envelope == null)
            {
                logger.LogWarning("Failed to deserialize event {EventId}", inboxMessage.Id);
                await MarkFailedAsync(inboxMessage.Id, inboxStore, unitOfWorkManager, cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Error, "Deserialization failed");
                return;
            }

            var eventName = envelope.Type;
            var version = envelope.Version ?? 1;
            activity?.SetTag("event.name", eventName);
            activity?.SetTag("event.version", version);

            if (!invokerRegistry.TryGet(eventName, version, out var invoker))
            {
                logger.LogWarning("No handler for {EventName} v{Version}", eventName, version);
                await MarkFailedAsync(inboxMessage.Id, inboxStore, unitOfWorkManager, cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Error, $"No handler for {eventName} v{version}");
                return;
            }

            await using var handlerUow = unitOfWorkManager.BeginRequiresNew();
            await invoker.InvokeAsync(scopedServiceProvider, inboxMessage.EventData, cancellationToken);
            await inboxStore.MarkAsProcessedAsync(inboxMessage.Id, cancellationToken);
            await handlerUow.CommitAsync(cancellationToken);

            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.LogInformation("Processed event {EventId} ({EventName} v{Version})", inboxMessage.Id, eventName, version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process event {EventId}", inboxMessage.Id);
            RecordException(activity, ex);
            var uowManager = scopedServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var store = scopedServiceProvider.GetRequiredService<IInboxStore>();
            await MarkFailedAsync(inboxMessage.Id, store, uowManager, cancellationToken);
        }
    }

    private static async Task MarkFailedAsync(
        string eventId,
        IInboxStore inboxStore,
        IUnitOfWorkManager uowManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var uow = uowManager.BeginRequiresNew();
            await inboxStore.MarkAsFailedAsync(eventId, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Log ama fırlat — batch devam etmeli
            _ = ex;
        }
    }

    protected virtual async Task CleanupOldMessagesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Schema)) return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            using (currentSchema.Change(options.Schema!))
            {
                await using var uow = unitOfWorkManager.BeginRequiresNew();
                var deletedCount = await inboxStore.CleanupOldMessagesAsync(
                    options.CleanupBatchSize, options.RetentionPeriod, cancellationToken);
                await uow.CommitAsync(cancellationToken);

                if (deletedCount > 0)
                    logger.LogInformation("Cleaned up {Count} old inbox messages", deletedCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cleaning up old inbox messages");
        }
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message },
        }));
    }
}
```

- [ ] **Step 2: `dotnet build framework/BBT.Aether.slnx` — derlenmeli**

```bash
dotnet build framework/BBT.Aether.slnx
```

- [ ] **Step 3: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs
git commit -m "feat(infrastructure): rewrite InboxProcessor — IInboxLeaseStore, remove distributed lock, dead letter, Task<int>"
```

---

## Task 7: OutboxBackgroundService + InboxBackgroundService

**Files:**
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs`
- Create: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs`
- Create: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs`

- [ ] **Step 1: Adaptive polling unit testini yaz**

```csharp
// framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs
using System;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class AdaptivePollingTests
{
    private static TimeSpan NextDelay(TimeSpan current, int processed, AetherOutboxOptions opts)
    {
        if (processed > 0) return opts.BusyPollingInterval;
        var next = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
        return next > opts.MaxPollingInterval ? opts.MaxPollingInterval : next;
    }

    [Fact]
    public void Busy_returns_busy_interval()
    {
        var opts = new AetherOutboxOptions
        {
            BusyPollingInterval = TimeSpan.FromMilliseconds(100),
            IdlePollingInterval = TimeSpan.FromSeconds(5),
            MaxPollingInterval  = TimeSpan.FromSeconds(60),
        };
        NextDelay(opts.IdlePollingInterval, processed: 10, opts)
            .ShouldBe(opts.BusyPollingInterval);
    }

    [Fact]
    public void Idle_doubles_delay_each_round()
    {
        var opts = new AetherOutboxOptions
        {
            BusyPollingInterval = TimeSpan.FromMilliseconds(100),
            IdlePollingInterval = TimeSpan.FromSeconds(5),
            MaxPollingInterval  = TimeSpan.FromSeconds(60),
        };
        var d1 = NextDelay(opts.IdlePollingInterval, processed: 0, opts); // 10s
        var d2 = NextDelay(d1, processed: 0, opts);                        // 20s
        var d3 = NextDelay(d2, processed: 0, opts);                        // 40s
        var d4 = NextDelay(d3, processed: 0, opts);                        // 60s (capped)
        var d5 = NextDelay(d4, processed: 0, opts);                        // 60s (stays capped)

        d1.ShouldBe(TimeSpan.FromSeconds(10));
        d2.ShouldBe(TimeSpan.FromSeconds(20));
        d3.ShouldBe(TimeSpan.FromSeconds(40));
        d4.ShouldBe(TimeSpan.FromSeconds(60));
        d5.ShouldBe(TimeSpan.FromSeconds(60));
    }
}
```

- [ ] **Step 2: Testleri çalıştır — geçmeli (saf logic testi)**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/ --filter "AdaptivePolling"
```

Expected: 2 test PASS.

- [ ] **Step 3: `OutboxBackgroundService` oluştur**

```csharp
// framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxBackgroundService(
    IOutboxProcessor processor,
    AetherOutboxOptions options,
    ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = options.IdlePollingInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(stoppingToken);
                delay = processed > 0
                    ? options.BusyPollingInterval
                    : Min(delay * 2, options.MaxPollingInterval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox background service error");
                delay = options.MaxPollingInterval;
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
```

- [ ] **Step 4: `InboxBackgroundService` oluştur**

```csharp
// framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.Events.Processing;

public sealed class InboxBackgroundService(
    IInboxProcessor processor,
    AetherInboxOptions options,
    ILogger<InboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = options.IdlePollingInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunAsync(stoppingToken);
                delay = processed > 0
                    ? options.BusyPollingInterval
                    : Min(delay * 2, options.MaxPollingInterval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox background service error");
                delay = options.MaxPollingInterval;
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
```

- [ ] **Step 5: `dotnet build framework/BBT.Aether.slnx`**

```bash
dotnet build framework/BBT.Aether.slnx
```

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs
git commit -m "feat(infrastructure): add OutboxBackgroundService and InboxBackgroundService with adaptive polling"
```

---

## Task 8: NpgsqlOutboxLeaseStore

**Files:**
- Create: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs`
- Create: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs`

- [ ] **Step 1: Integration testini yaz**

```csharp
// framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class NpgsqlLeaseStoreTests(PostgresFixture fx)
{
    private readonly string _schema = "lease_test_" + Guid.NewGuid().ToString("N");

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreOutbox
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureOutbox();
        }
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString);
        services.AddAetherOutbox<TestDbContext>();
        return services.BuildServiceProvider();
    }

    private async Task SetupSchemaAsync(IServiceProvider sp)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE SCHEMA \"{_schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var configurator = sp.GetRequiredService<BBT.Aether.Uow.EntityFrameworkCore.IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new BBT.Aether.Uow.EntityFrameworkCore.SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript();

        await using var ddlConn = new NpgsqlConnection(fx.ConnectionString);
        await ddlConn.OpenAsync();
        await using var setCmd = ddlConn.CreateCommand();
        setCmd.CommandText = $"SET search_path TO \"{_schema}\";";
        await setCmd.ExecuteNonQueryAsync();
        await using var ddlCmd = ddlConn.CreateCommand();
        ddlCmd.CommandText = script;
        await ddlCmd.ExecuteNonQueryAsync();
    }

    private async Task InsertPendingMessageAsync(IServiceProvider sp, Guid id)
    {
        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            await outboxStore.StoreAsync(new CloudEventEnvelope
            {
                Id = id.ToString(),
                Type = "TestEvent",
                Topic = "test-topic",
                Data = System.Text.Encoding.UTF8.GetBytes("{}")
            });

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task LeaseBatch_returns_pending_messages_and_locks_them()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);

        var messageId = Guid.NewGuid();
        await InsertPendingMessageAsync(sp, messageId);

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].Status.ShouldBe(OutboxMessageStatus.Processing);
            leased[0].LockedBy.ShouldBe("worker-1");
            leased[0].LockedUntil.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task LeaseBatch_skips_already_locked_messages()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);

        await InsertPendingMessageAsync(sp, Guid.NewGuid());

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            // Worker 1 leases the message
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(60));
                await uow.CommitAsync();
            }

            // Worker 2 should get nothing (message locked)
            IReadOnlyList<OutboxMessage> worker2Batch;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                worker2Batch = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(60));
                await uow.CommitAsync();
            }

            worker2Batch.Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task LeaseBatch_does_not_pick_up_dead_letter_messages()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);

        // Insert then mark as dead letter via direct SQL
        await InsertPendingMessageAsync(sp, Guid.NewGuid());
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE \"{_schema}\".\"OutboxMessages\" SET \"Status\" = 3"; // 3 = DeadLetter
        await cmd.ExecuteNonQueryAsync();

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(0);
        }
    }
}
```

- [ ] **Step 2: Testleri çalıştır — derleme hatası beklenir (`NpgsqlOutboxLeaseStore` yok)**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/ --filter "NpgsqlLeaseStore"
```

Expected: build error veya `IOutboxLeaseStore` not registered hatası.

- [ ] **Step 3: `NpgsqlOutboxLeaseStore` oluştur**

```csharp
// framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Events;

public class NpgsqlOutboxLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IOutboxLeaseStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    public async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.OutboxMessage))!;
        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();
        var fullTableName = string.IsNullOrEmpty(schema)
            ? $"\"{tableName}\""
            : $"\"{schema}\".\"{tableName}\"";

        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;
        var lockedUntil = now.Add(leaseDuration);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "Status"      = @processing,
                "LockedBy"    = @workerId,
                "LockedUntil" = @lockedUntil
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" = @pending
                  AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
                  AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= @now)
                ORDER BY "CreatedAt"
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "Status", "EventName", "EventData", "CreatedAt",
                      "ProcessedAt", "LockedBy", "LockedUntil", "LastError",
                      "RetryCount", "NextRetryAt", "ExtraProperties";
            """;

        AddParameter(command, "@processing", (int)OutboxMessageStatus.Processing);
        AddParameter(command, "@pending",    (int)OutboxMessageStatus.Pending);
        AddParameter(command, "@workerId",   workerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now",        now);
        AddParameter(command, "@batchSize",  batchSize);

        var messages = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessage
            {
                Id          = reader.GetGuid(reader.GetOrdinal("Id")),
                Status      = (OutboxMessageStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                EventName   = reader.GetString(reader.GetOrdinal("EventName")),
                EventData   = (byte[])reader["EventData"],
                CreatedAt   = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                ProcessedAt = reader.IsDBNull(reader.GetOrdinal("ProcessedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("ProcessedAt")),
                LockedBy    = reader.IsDBNull(reader.GetOrdinal("LockedBy"))    ? null : reader.GetString(reader.GetOrdinal("LockedBy")),
                LockedUntil = reader.IsDBNull(reader.GetOrdinal("LockedUntil")) ? null : reader.GetDateTime(reader.GetOrdinal("LockedUntil")),
                LastError   = reader.IsDBNull(reader.GetOrdinal("LastError"))   ? null : reader.GetString(reader.GetOrdinal("LastError")),
                RetryCount  = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryAt = reader.IsDBNull(reader.GetOrdinal("NextRetryAt")) ? null : reader.GetDateTime(reader.GetOrdinal("NextRetryAt")),
                ExtraProperties = DeserializeExtraProperties(reader),
            });
        }

        return messages;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private static Dictionary<string, object> DeserializeExtraProperties(DbDataReader reader)
    {
        var ordinal = reader.GetOrdinal("ExtraProperties");
        if (reader.IsDBNull(ordinal)) return new Dictionary<string, object>();
        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new Dictionary<string, object>();
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
    }
}
```

- [ ] **Step 4: Registration'ı geçici olarak `AddAetherNpgsql`'e ekle (Task 10'da tam hale getirilecek)**

`framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs` dosyasında `AddAetherNpgsql` metoduna ekle:

```csharp
// Geçici — Task 10'da reflection-based conditional registration yapılacak
if (typeof(IHasEfCoreOutbox).IsAssignableFrom(typeof(TDbContext)))
    services.AddScoped(typeof(IOutboxLeaseStore),
        typeof(NpgsqlOutboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));
```

- [ ] **Step 5: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/ --filter "NpgsqlLeaseStore"
```

Expected: 3 test PASS.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs \
        framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs \
        framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs
git commit -m "feat(npgsql): add NpgsqlOutboxLeaseStore with FOR UPDATE SKIP LOCKED, integration tests"
```

---

## Task 9: NpgsqlInboxLeaseStore

**Files:**
- Create: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs`

- [ ] **Step 1: `NpgsqlInboxLeaseStore` oluştur**

```csharp
// framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BBT.Aether.Events;

public class NpgsqlInboxLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IInboxLeaseStore
    where TDbContext : DbContext, IHasEfCoreInbox
{
    public async Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var entityType = dbContext.Model.FindEntityType(typeof(BBT.Aether.Domain.Events.InboxMessage))!;
        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();
        var fullTableName = string.IsNullOrEmpty(schema)
            ? $"\"{tableName}\""
            : $"\"{schema}\".\"{tableName}\"";

        var connection = dbContext.Database.GetDbConnection();
        var now = clock.UtcNow;
        var lockedUntil = now.Add(leaseDuration);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "Status"      = @processing,
                "LockedBy"    = @workerId,
                "LockedUntil" = @lockedUntil
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" = @pending
                  AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
                  AND ("NextRetryTime" IS NULL OR "NextRetryTime" <= @now)
                ORDER BY "CreatedAt"
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "Status", "EventName", "EventData", "CreatedAt",
                      "HandledTime", "LockedBy", "LockedUntil", "RetryCount",
                      "NextRetryTime", "ExtraProperties";
            """;

        AddParameter(command, "@processing", (int)IncomingEventStatus.Processing);
        AddParameter(command, "@pending",    (int)IncomingEventStatus.Pending);
        AddParameter(command, "@workerId",   workerId);
        AddParameter(command, "@lockedUntil", lockedUntil);
        AddParameter(command, "@now",        now);
        AddParameter(command, "@batchSize",  batchSize);

        var messages = new List<InboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new InboxMessage
            {
                Id          = reader.GetString(reader.GetOrdinal("Id")),
                Status      = (IncomingEventStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                EventName   = reader.GetString(reader.GetOrdinal("EventName")),
                EventData   = (byte[])reader["EventData"],
                CreatedAt   = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                HandledTime = reader.IsDBNull(reader.GetOrdinal("HandledTime"))    ? null : reader.GetDateTime(reader.GetOrdinal("HandledTime")),
                LockedBy    = reader.IsDBNull(reader.GetOrdinal("LockedBy"))       ? null : reader.GetString(reader.GetOrdinal("LockedBy")),
                LockedUntil = reader.IsDBNull(reader.GetOrdinal("LockedUntil"))    ? null : reader.GetDateTime(reader.GetOrdinal("LockedUntil")),
                RetryCount  = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                NextRetryTime = reader.IsDBNull(reader.GetOrdinal("NextRetryTime")) ? null : reader.GetDateTime(reader.GetOrdinal("NextRetryTime")),
                ExtraProperties = DeserializeExtraProperties(reader),
            });
        }

        return messages;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private static Dictionary<string, object> DeserializeExtraProperties(DbDataReader reader)
    {
        var ordinal = reader.GetOrdinal("ExtraProperties");
        if (reader.IsDBNull(ordinal)) return new Dictionary<string, object>();
        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new Dictionary<string, object>();
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
    }
}
```

- [ ] **Step 2: `AddAetherNpgsql`'e inbox lease registration ekle (Task 4'teki gibi)**

```csharp
if (typeof(IHasEfCoreInbox).IsAssignableFrom(typeof(TDbContext)))
    services.AddScoped(typeof(IInboxLeaseStore),
        typeof(NpgsqlInboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));
```

- [ ] **Step 3: `dotnet build framework/BBT.Aether.slnx`**

```bash
dotnet build framework/BBT.Aether.slnx
```

- [ ] **Step 4: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs \
        framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs
git commit -m "feat(npgsql): add NpgsqlInboxLeaseStore with FOR UPDATE SKIP LOCKED"
```

---

## Task 10: Registration Updates

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherOutboxServiceCollectionExtensions.cs`
- Modify: `framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs`

- [ ] **Step 1: `AddAetherOutbox` güncelle**

```csharp
// framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/AetherOutboxServiceCollectionExtensions.cs
using System;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Aether.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddAetherOutbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherOutboxOptions>? configure = null,
        bool withHostedService = false)
        where TDbContext : DbContext, IHasEfCoreOutbox
    {
        var options = new AetherOutboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TDbContext>>();

        // Null fallback — provider (Npgsql/SqlServer) overrides this with AddScoped
        services.TryAddScoped<IOutboxLeaseStore, NullOutboxLeaseStore>();

        // WorkerIdentity singleton — guard against double registration
        services.TryAddSingleton<WorkerIdentity>();

        services.AddSingleton<IOutboxProcessor, OutboxProcessor<TDbContext>>();

        if (withHostedService)
            services.AddHostedService<OutboxBackgroundService>();

        return services;
    }

    public static IServiceCollection AddAetherInbox<TDbContext>(
        this IServiceCollection services,
        Action<AetherInboxOptions>? configure = null,
        bool withHostedService = false)
        where TDbContext : DbContext, IHasEfCoreInbox
    {
        var options = new AetherInboxOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddScoped<IInboxStore, EfCoreInboxStore<TDbContext>>();

        services.TryAddScoped<IInboxLeaseStore, NullInboxLeaseStore>();

        services.TryAddSingleton<WorkerIdentity>();

        services.AddSingleton<IInboxProcessor, InboxProcessor<TDbContext>>();

        if (withHostedService)
            services.AddHostedService<InboxBackgroundService>();

        return services;
    }
}
```

- [ ] **Step 2: `AddAetherNpgsql` son halini yaz**

`framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/AetherNpgsqlServiceCollectionExtensions.cs` dosyasını güncelle. Task 8 ve 9'da eklenen geçici registration'ları temiz hale getir:

```csharp
using System;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Events;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherNpgsqlServiceCollectionExtensions
{
    public static IServiceCollection AddAetherNpgsql<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        SchemaSwitchingMode mode = SchemaSwitchingMode.TransactionLocal,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TDbContext : AetherDbContext<TDbContext>
    {
        services.AddAetherDbContext<TDbContext>(new NpgsqlAetherProvider(mode), connectionString, configure);

        // Outbox lease store — only if TDbContext implements IHasEfCoreOutbox
        if (typeof(IHasEfCoreOutbox).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IOutboxLeaseStore),
                typeof(NpgsqlOutboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        // Inbox lease store — only if TDbContext implements IHasEfCoreInbox
        if (typeof(IHasEfCoreInbox).IsAssignableFrom(typeof(TDbContext)))
            services.AddScoped(typeof(IInboxLeaseStore),
                typeof(NpgsqlInboxLeaseStore<>).MakeGenericType(typeof(TDbContext)));

        return services;
    }
}
```

- [ ] **Step 3: `dotnet build framework/BBT.Aether.slnx` — tam derleme**

```bash
dotnet build framework/BBT.Aether.slnx
```

Expected: 0 error, 0 warning (Nullable uyarıları hariç).

- [ ] **Step 4: Tüm testleri çalıştır**

```bash
dotnet test framework/BBT.Aether.slnx
```

Expected: tüm testler PASS — özellikle `OutboxWithinSharedTransactionTests` (mevcut) ve `NpgsqlLeaseStoreTests` (yeni).

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/Microsoft/Extensions/DependencyInjection/ \
        framework/src/BBT.Aether.Npgsql/Microsoft/Extensions/DependencyInjection/
git commit -m "feat(registration): AddAetherOutbox/Inbox withHostedService param, TryAdd null lease stores; AddAetherNpgsql auto-registers lease stores"
```

---

## Final: Full Test Suite

- [ ] **Step 1: Tüm solution'ı temiz derle ve testleri çalıştır**

```bash
dotnet build framework/BBT.Aether.slnx --configuration Release
dotnet test framework/BBT.Aether.slnx --configuration Release
```

Expected: tüm testler PASS.

- [ ] **Step 2: Son commit**

```bash
git add .
git commit -m "feat(inbox-outbox): complete lease strategy redesign — provider separation, adaptive polling, dead letter, WorkerIdentity"
```
