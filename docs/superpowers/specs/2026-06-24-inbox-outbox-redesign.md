# Inbox & Outbox Redesign — Spec

**Date:** 2026-06-24  
**Branch:** feat/schema-switching-modes  
**Status:** Approved

---

## Problem

Mevcut `EfCoreOutboxStore` ve `EfCoreInboxStore` implementasyonları `BBT.Aether.Infrastructure` (provider-agnostic) içinde PostgreSQL-specific `FOR UPDATE SKIP LOCKED` raw SQL barındırıyor. Bu durumun yarattığı sorunlar:

1. **Provider bağımlılığı yanlış pakette** — Infrastructure, Npgsql import etmeden PostgreSQL syntax kullanıyor.
2. **SQL Server desteği yok** — `EfCoreOutboxStore` doğrudan PostgreSQL üzerinde çalışır; SQL Server için override noktası belirsiz.
3. **Sabit polling** — 30 saniyelik sabit interval, scale ortamında boşta bile DB'ye sürekli bağlantı açıyor.
4. **Phase 3 race condition** — OutboxProcessor outcome yazarken `LockedBy` guard yok; expired lease sonrası başka worker lease aldıysa eski sonuç ezebilir.
5. **Dead letter yok** — MaxRetryCount aşıldığında mesaj `Pending`'de kalır; SQL'de hardcoded `RetryCount < 10` ile options'tan okunan `MaxRetryCount` arasında tutarsızlık var.
6. **InboxProcessor gereksiz distributed lock** — Processing koordinasyonu için distributed lock kullanıyor; lease zaten bu koordinasyonu sağlıyor.
7. **WorkerIdentity yapısal değil** — `MachineName-ProcessId-Guid` formatı diagnostics için yetersiz.
8. **HostedService yok** — Kullanıcı kendi `BackgroundService` wrapper'ını yazmak zorunda.

**Bu fazda SQL Server desteği kapsam dışıdır.** PostgreSQL fazı tamamlandıktan sonra ayrı fazda ele alınacak.

---

## Karar

**Yaklaşım A — Ayrı Lease Interface'leri:** `IOutboxLeaseStore` ve `IInboxLeaseStore` ayrı interface olarak Abstractions'a eklenir. PostgreSQL implementasyonu `BBT.Aether.Npgsql` paketine taşınır. `EfCoreOutboxStore` / `EfCoreInboxStore` sadece provider-agnostic write/mark operasyonlarını barındırır.

---

## Bölüm 1 — Interface & Contract Değişiklikleri

### Yeni: `IOutboxLeaseStore` (Abstractions)

```csharp
public interface IOutboxLeaseStore
{
    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);
}
```

### Yeni: `IInboxLeaseStore` (Abstractions)

```csharp
public interface IInboxLeaseStore
{
    Task<IReadOnlyList<InboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);
}
```

### Değişen: `IOutboxStore`

`LeaseBatchAsync` kaldırılır — sadece write operasyonu:

```csharp
public interface IOutboxStore
{
    Task StoreAsync(CloudEventEnvelope envelope, CancellationToken ct = default);
}
```

### Değişen: `IInboxStore`

Artık kullanılmayan metodlar ve `LeaseBatchAsync` kaldırılır:

```csharp
public interface IInboxStore
{
    Task StorePendingAsync(CloudEventEnvelope envelope, CancellationToken ct = default);
    Task<bool> HasProcessedAsync(string eventId, CancellationToken ct = default);
    Task MarkAsProcessedAsync(string eventId, CancellationToken ct = default);
    Task MarkAsFailedAsync(string eventId, CancellationToken ct = default);
    Task<int> CleanupOldMessagesAsync(int batchSize, TimeSpan retentionPeriod, CancellationToken ct = default);
    // KALDIRILDI: GetPendingEventsAsync   (LeaseBatchAsync ile örtüşüyordu)
    // KALDIRILDI: MarkAsProcessingAsync   (lease zaten Processing'e alıyor)
    // KALDIRILDI: LeaseBatchAsync         (IInboxLeaseStore'a taşındı)
}
```

### Değişen: Status Enum'ları

```csharp
public enum OutboxMessageStatus
{
    Pending    = 0,
    Processing = 1,
    Processed  = 2,
    DeadLetter = 3,   // YENİ
}

public enum IncomingEventStatus
{
    Pending    = 0,
    Processing = 1,
    Processed  = 2,
    Discarded  = 3,
    DeadLetter = 4,   // YENİ
}
```

> Migration gerekmez. Yeni enum değerleri mevcut `Status` int kolonu kapsamında çalışır.

---

## Bölüm 2 — Infrastructure Katmanı

### `EfCoreOutboxStore` (Infrastructure)

Raw SQL ve `LeaseBatchAsync` kaldırılır. Sadece `StoreAsync` kalır.

### `EfCoreInboxStore` (Infrastructure)

Raw SQL, `LeaseBatchAsync`, `GetPendingEventsAsync`, `MarkAsProcessingAsync` kaldırılır. Write + mark operasyonları EF Core LINQ ile kalır.

### `WorkerIdentity` (Infrastructure) — YENİ

Singleton, uygulama startup'ta bir kez üretilir:

```csharp
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

Kullanım:
- Outbox processor: `$"{workerIdentity.Value}/outbox"`
- Inbox processor: `$"{workerIdentity.Value}/inbox"`

### `IOutboxProcessor` ve `IInboxProcessor` (Core)

`RunAsync` dönüş tipi `Task` → `Task<int>` olarak değişir (adaptive polling için işlenen mesaj sayısı):

```csharp
public interface IOutboxProcessor { Task<int> RunAsync(CancellationToken ct = default); }
public interface IInboxProcessor  { Task<int> RunAsync(CancellationToken ct = default); }
```

### `OutboxProcessor` (Infrastructure)

- `IOutboxLeaseStore` inject edilir (lease), `IOutboxStore` sadece write için kalır.
- **Phase 3 guard:** Outcome yazımında EF Core `ExecuteUpdateAsync` ile `WHERE` koşulu eklenir (provider-agnostic):

```csharp
var affected = await dbContext.OutboxMessages
    .Where(m => m.Id == outcome.MessageId
             && m.LockedBy == _workerId
             && m.LockedUntil > now)
    .ExecuteUpdateAsync(s => s
        .SetProperty(m => m.Status, OutboxMessageStatus.Processed)
        .SetProperty(m => m.ProcessedAt, now)
        .SetProperty(m => m.LockedBy, (string?)null)
        .SetProperty(m => m.LockedUntil, (DateTime?)null), ct);

// affected == 0 → lease süresi dolmuş ya da başka worker aldı → sadece logla
```

- **Dead Letter logic:** `domainMessage.RetryCount >= options.MaxRetryCount` ise status `DeadLetter`.
- `RunAsync` dönüş tipi `Task<int>` olur — işlenen mesaj sayısı (adaptive polling için).

### `InboxProcessor` (Infrastructure)

- `IDistributedLockService` constructor'dan kaldırılır.
- Processing döngüsü tamamen lease-based.
- `MarkAsFailedAsync` içinde `RetryCount >= options.MaxRetryCount` kontrolü → `DeadLetter`.
- `RunAsync` dönüş tipi `int` olur.

### `OutboxBackgroundService<TDbContext>` (Infrastructure) — YENİ

Adaptive polling ile `IOutboxProcessor.RunAsync` döngüsünü yönetir:

```csharp
public class OutboxBackgroundService<TDbContext> : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = options.IdlePollingInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await processor.RunAsync(stoppingToken);

            delay = processed > 0
                ? options.BusyPollingInterval
                : TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2,
                             options.MaxPollingInterval.TotalMilliseconds));

            await Task.Delay(delay, stoppingToken);
        }
    }
}
```

`InboxBackgroundService<TDbContext>` aynı yapıda.

### Options Değişiklikleri

**`AetherOutboxOptions`:**

```csharp
// YENİ
public TimeSpan BusyPollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);
public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(5);
public TimeSpan MaxPollingInterval  { get; set; } = TimeSpan.FromSeconds(60);

// KALDIRILDI: ProcessingInterval (artık kullanılmıyor)
```

**`AetherInboxOptions`:**

```csharp
// YENİ
public TimeSpan BusyPollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);
public TimeSpan IdlePollingInterval { get; set; } = TimeSpan.FromSeconds(5);
public TimeSpan MaxPollingInterval  { get; set; } = TimeSpan.FromSeconds(60);

// KALDIRILDI: ProcessingInterval, DistributedLockName, LockExpirySeconds
```

### Registration Değişiklikleri

```csharp
services.AddAetherOutbox<MyDbContext>(options =>
{
    options.BatchSize     = 100;
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.MaxRetryCount = 5;
}, withHostedService: true);  // false → kullanıcı kendi yönetir

services.AddAetherInbox<MyDbContext>(options =>
{
    options.ProcessingBatchSize = 100;
    options.MaxRetryCount       = 5;
}, withHostedService: true);
```

`WorkerIdentity` her iki registration içinde singleton olarak kaydedilir (çift kayıt önlenecek).

---

## Bölüm 3 — Npgsql Katmanı

### `NpgsqlOutboxLeaseStore<TDbContext>` (BBT.Aether.Npgsql) — YENİ

```csharp
public class NpgsqlOutboxLeaseStore<TDbContext>(
    IAetherDbContextProvider<TDbContext> dbContextProvider,
    IClock clock) : IOutboxLeaseStore
    where TDbContext : DbContext, IHasEfCoreOutbox
{
    // FOR UPDATE SKIP LOCKED
    // WHERE Status = Pending
    //   AND (LockedUntil IS NULL OR LockedUntil < now)
    //   AND (NextRetryAt IS NULL OR NextRetryAt <= now)
    //   -- RetryCount < 10 KALDIRILDI
    public Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(...);
}
```

### `NpgsqlInboxLeaseStore<TDbContext>` (BBT.Aether.Npgsql) — YENİ

Aynı yapıda, `InboxMessage` için.

### `AddAetherNpgsql` Registration Değişikliği

```csharp
// Mevcut registration'a eklenir:
services.AddScoped<IOutboxLeaseStore, NpgsqlOutboxLeaseStore<TDbContext>>();
services.AddScoped<IInboxLeaseStore, NpgsqlInboxLeaseStore<TDbContext>>();
```

---

## Değişiklik Özeti

| Paket | Dosya | Değişiklik |
|-------|-------|-----------|
| Abstractions | `IOutboxStore` | `LeaseBatchAsync` kaldırıldı |
| Abstractions | `IInboxStore` | 3 metod kaldırıldı |
| Abstractions | `IOutboxLeaseStore` | YENİ |
| Abstractions | `IInboxLeaseStore` | YENİ |
| Abstractions | `OutboxMessageStatus` | `DeadLetter` eklendi |
| Abstractions | `IncomingEventStatus` | `DeadLetter` eklendi |
| Infrastructure | `EfCoreOutboxStore` | Raw SQL + `LeaseBatchAsync` kaldırıldı |
| Infrastructure | `EfCoreInboxStore` | Raw SQL + 3 metod kaldırıldı |
| Infrastructure | `OutboxProcessor` | `IOutboxLeaseStore`, phase 3 guard, DeadLetter, `int` dönüş |
| Infrastructure | `InboxProcessor` | `IInboxLeaseStore`, distributed lock kaldırıldı, DeadLetter, `int` dönüş |
| Infrastructure | `WorkerIdentity` | YENİ singleton |
| Infrastructure | `OutboxBackgroundService` | YENİ adaptive polling |
| Infrastructure | `InboxBackgroundService` | YENİ adaptive polling |
| Infrastructure | `AetherOutboxOptions` | Polling alanları yenilendi |
| Infrastructure | `AetherInboxOptions` | Polling + lock alanları yenilendi |
| Infrastructure | `AddAetherOutbox` | `withHostedService` parametresi |
| Infrastructure | `AddAetherInbox` | `withHostedService` parametresi |
| Npgsql | `NpgsqlOutboxLeaseStore` | YENİ — raw SQL buraya taşındı |
| Npgsql | `NpgsqlInboxLeaseStore` | YENİ — raw SQL buraya taşındı |
| Npgsql | `AddAetherNpgsql` | Lease store registration eklendi |

---

## Kapsam Dışı (Sonraki Faz)

- SQL Server: `SqlServerOutboxLeaseStore`, `SqlServerInboxLeaseStore` — `UPDLOCK, READPAST, ROWLOCK` + `OUTPUT INSERTED.*`
