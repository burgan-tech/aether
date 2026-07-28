# Outbox/Inbox Faz 1 — Stale-Lease Bug Fix + DB Maliyeti Azaltma + Partition Yazma Yolu

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Süresi dolmuş lease'lerin hiç geri alınmaması bug'ını düzeltmek (aktif veri kaybı), outbox/inbox dispatcher döngüsünün DB transaction maliyetini ~3× azaltmak ve `PartitionId` kolonunu tablolar küçükken doldurmaya başlamak.

**Architecture:** Değişikliklerin tamamı Aether SDK'da (`BBT.Aether.Core`, `BBT.Aether.Abstractions`, `BBT.Aether.Infrastructure`, `BBT.Aether.Npgsql`) — vNext yalnızca EF migration'ı üretir ve appsettings'i günceller. Faz 1 davranış-koruyucudur: yeni kavram, yeni endpoint, yeni bağımlılık yok. `PartitionId` yazılır ama hiçbir sorguda kullanılmaz (okuma yolu Faz 3). Sinyal mekanizması Faz 2'de, ayrı planda.

**Tech Stack:** .NET 10, EF Core 10.0.4, Npgsql 10.0.2, xUnit + Shouldly + NSubstitute, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-07-28-outbox-signal-partition-design.md` (§4 bulgular, §6 Faz 1)

> ⚠️ **Enum'lar aynı değil — koddan doğrulandı.** Inbox ve outbox durum değerleri birebir
> örtüşmüyor; sayı hardcode ederken buna dikkat:
>
> | | Pending | Processing | Processed | Discarded | DeadLetter |
> |---|---|---|---|---|---|
> | `OutboxMessageStatus` | 0 | 1 | 2 | — | **3** |
> | `IncomingEventStatus` (inbox) | 0 | 1 | 2 | 3 | **4** |
>
> Dispatch partial index'i (`Status IN (0, 1)`) ve cleanup index'i (`Status = 2`) her iki
> tarafta da doğru — çakışma yalnızca `DeadLetter`'da. Lease sorguları parametre kullandığı
> için etkilenmiyor.

**Repos:**
- Aether: `/Users/U0B006/Documents/repos/burgan-tech/aether`
- vNext: `/Users/U0B006/Documents/repos/burgan-tech/vnext`

---

## File Structure

### Aether — değiştirilecek

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs` | Outbox lease SQL | T1 |
| `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs` | Inbox lease SQL | T3 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs` | Dead-letter guard + cleanup gating | T2, T5 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs` | Cleanup gating | T6 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs` | Backoff | T4 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs` | Backoff | T4 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs` | `CleanupInterval`, `CleanupBatchSize`, `PartitionCount` | T5, T9 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherInboxOptions.cs` | `PartitionCount` | T9 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs` | `PartitionId` yazma + cleanup | T7, T9 |
| `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs` | `PartitionId` yazma + cleanup | T7, T9 |
| `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxStore.cs` | Cleanup metodu | T7 |
| `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxMessage.cs` | `PartitionId` (DTO) | T8 |
| `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/InboxMessage.cs` | `PartitionId` (DTO) | T8 |
| `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Events/OutboxMessage.cs` | `PartitionId` (entity) | T8 |
| `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Events/InboxMessage.cs` | `PartitionId` (entity) | T8 |
| `.../Modeling/OutboxModelBuilderExtensions.cs` | Kolon + partial index | T8 |
| `.../Modeling/InboxModelBuilderExtensions.cs` | Kolon + partial index | T8 |

### Aether — yeni

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/AdaptivePolling.cs` | Poll gecikme politikası (saf fonksiyon, inbox+outbox paylaşır) | T4 |
| `framework/src/BBT.Aether.Core/BBT/Aether/Events/MessagePartitionResolver.cs` | Deterministic xxHash64 partition | T9 |

### Aether — testler

| Dosya | Görev |
|---|---|
| `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs` (mevcut, eklenecek) | T1, T2 |
| `framework/test/BBT.Aether.Postgres.Tests/NpgsqlInboxLeaseStoreTests.cs` (yeni) | T3 |
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs` (mevcut, değişecek) | T4 |
| `framework/test/BBT.Aether.Postgres.Tests/OutboxCleanupTests.cs` (yeni) | T5, T7 |
| `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/MessagePartitionResolverTests.cs` (yeni) | T9 |

### vNext

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `src/BBT.Workflow.Infrastructure/Migrations/MessagingDb/` (yeni migration) | `PartitionId` + partial index | T10 |
| `workers/BBT.Workflow.Workers.Outbox/appsettings.json` | `CleanupInterval`, `CleanupBatchSize`, retention | T11 |
| `workers/BBT.Workflow.Workers.Inbox/appsettings.json` | retention | T11 |

---

## Ön Koşul: Branch

- [ ] **Step 0: Aether'da feature branch aç**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
git checkout -b feature/outbox-faz1-lease-fix
```

---

## Task 1: Süresi dolmuş lease'i geri al — Outbox

**Neden:** `NpgsqlOutboxLeaseStore` lease alırken `Status = Processing` yazıyor ama aday sorgusu `WHERE "Status" = @pending`. Yani `LockedUntil < now` koşulu ölü kod; `Processing`'de kalan satırlar hiç yayınlanmıyor. Preprod loglarında outbox döngülerinin %33'ü DB connect timeout ile kesiliyor — bu bug her kesintide veri kaybı üretiyor.

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs:56-66`
- Test: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`NpgsqlLeaseStoreTests.cs` içine, mevcut `LeaseBatch_does_not_pick_up_dead_letter_messages` testinin altına ekle:

```csharp
    [Fact]
    public async Task LeaseBatch_reclaims_processing_message_with_expired_lease()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // Bir worker leaseledi ve çöktü: Status=Processing, LockedUntil geçmişte
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' - interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].LockedBy.ShouldBe("worker-2");
            // Reclaim edilen satırın RetryCount'u artmalı — crash-loop'ta sonsuz reclaim olmasın
            leased[0].RetryCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task LeaseBatch_does_not_reclaim_processing_message_with_valid_lease()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'healthy-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' + interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task LeaseBatch_does_not_increment_retry_count_for_fresh_pending()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].RetryCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task LeaseBatch_reclaims_processing_message_with_null_lock_expiry()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = NULL
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].LockedBy.ShouldBe("worker-2");
            leased[0].RetryCount.ShouldBe(1);
        }
    }
```

Ayrıca `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs` içindeki
`LeaseBatchAsync` XML özetine bir `<remarks>` eklenir: metot artık yalnızca leaselemekle
kalmıyor, yarıda kalmış satırları geri alıp `RetryCount`'u artırıyor — arayüz sözleşmesi
değişti.

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~NpgsqlLeaseStoreTests.LeaseBatch_reclaims_processing_message_with_expired_lease"
```

Beklenen: FAIL — `leased.Count` 1 yerine **0** gelir (mevcut sorgu `Processing` satırlarına bakmıyor).

`LeaseBatch_does_not_reclaim_processing_message_with_valid_lease` ve `LeaseBatch_does_not_increment_retry_count_for_fresh_pending` şu an **geçer** — bunlar regresyon koruması.

- [ ] **Step 3: Lease SQL'ini düzelt**

`NpgsqlOutboxLeaseStore.cs` içinde `command.CommandText` atamasını tamamen şununla değiştir:

```csharp
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "RetryCount"  = CASE WHEN "Status" = @processing
                                     THEN "RetryCount" + 1
                                     ELSE "RetryCount" END,
                "Status"      = @processing,
                "LockedBy"    = @workerId,
                "LockedUntil" = @lockedUntil
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" IN (@pending, @processing)
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
```

**Neden `SET` içindeki `"Status"` eski değeri görüyor:** PostgreSQL'de `UPDATE ... SET` ifadeleri satırın **güncelleme öncesi** değerleri üzerinden değerlendirilir. Bu yüzden `CASE WHEN "Status" = @processing` reclaim olup olmadığını doğru ayırt eder.

**Neden iki ayrı dal değil de `Status IN (...)`:** Pending ve Processing dalları aynı
`LockedUntil` koşulunu paylaşıyor. Ayrı yazılırsa `Processing` + `LockedUntil IS NULL` satırı
hiçbir dala uymaz ve kalıcı olarak reclaim edilemez — düzeltilen bug'ın aynısı, yer değiştirmiş
hâli. Birleşik form hem bunu kapatıyor hem de T8'de gelen partial index'e
(`WHERE "Status" IN (0,1)`) birebir uyuyor; ayrı dallar BitmapOr'a zorlardı.

Doğruluk tablosu (değişmemeli): Pending+NULL ✓, Pending+geçmiş ✓, Pending+gelecek ✗,
Processing+NULL ✓ (**düzeltme**), Processing+geçmiş ✓, Processing+gelecek ✗,
Processed ✗, DeadLetter ✗.

- [ ] **Step 4: Testleri çalıştır, geçtiklerini gör**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~NpgsqlLeaseStoreTests"
```

Beklenen: Tüm `NpgsqlLeaseStoreTests` testleri PASS (mevcut 4 + yeni 3 = 7).

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs \
        framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs
git commit -m "fix(outbox): reclaim expired leases stuck in Processing status

Lease query only matched Status=Pending, so the LockedUntil expiry check was
dead code. A worker crashing between lease and outcome-write left rows in
Processing forever, never published. Reclaim now also matches Processing rows
with an expired lease and increments RetryCount so crash-loops terminate."
```

---

## Task 2: Retry'ı tükenmiş reclaim'leri dead-letter'a düşür

**Neden:** T1 reclaim'de `RetryCount` artırıyor ama lease sorgusu üst sınır bilmiyor. Sürekli çöken bir mesaj sonsuza kadar reclaim edilir. Politika `MaxRetryCount`'u zaten bilen `OutboxProcessor`'a ait.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs:80-88`
- Test: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`NpgsqlLeaseStoreTests.cs`'e ekle:

```csharp
    [Fact]
    public async Task LeaseBatch_still_reclaims_when_retry_count_at_max_so_processor_can_dead_letter()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // RetryCount zaten max'ta (varsayılan MaxRetryCount = 5) ve lease süresi dolmuş
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 1,
                    "RetryCount" = 5,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' - interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            // Lease store filtrelemiyor — processor dead-letter'a düşürecek (T2)
            leased.Count.ShouldBe(1);
            leased[0].RetryCount.ShouldBe(6);
        }
    }
```

- [ ] **Step 2: Testi çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~LeaseBatch_still_reclaims_when_retry_count_at_max"
```

Beklenen: PASS (T1 sonrası zaten böyle davranıyor — bu bir sözleşme testi, T2'nin varsayımını sabitliyor).

- [ ] **Step 3: Processor'a dead-letter guard ekle**

`OutboxProcessor.cs`'de, `if (messages.Count == 0) return 0;` satırından hemen sonra gelen `logger.LogInformation("Leased {Count} ...")` satırının **altına** ekle:

```csharp
            // Retry bütçesi tükenmiş reclaim'ler publish edilmez, doğrudan dead-letter'a düşer.
            var exhausted = messages.Where(m => m.RetryCount > options.MaxRetryCount).ToList();
            var publishable = messages.Where(m => m.RetryCount <= options.MaxRetryCount).ToList();

            if (exhausted.Count > 0)
            {
                logger.LogWarning(
                    "Dead-lettering {Count} outbox messages whose retry budget is exhausted (MaxRetryCount={Max})",
                    exhausted.Count, options.MaxRetryCount);
            }
```

Ardından PHASE 2 döngüsünün başlığını `foreach (var message in messages)` yerine `foreach (var message in publishable)` yap.

PHASE 3'te, `await using (var updateUow = ...)` bloğunun içinde, `foreach (var outcome in outcomes)` döngüsünden **önce** ekle:

```csharp
                if (exhausted.Count > 0)
                {
                    var exhaustedIds = exhausted.Select(m => m.Id).ToList();
                    await dbContext.OutboxMessages
                        .Where(m => exhaustedIds.Contains(m.Id) && m.LockedBy == workerId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.Status, OutboxMessageStatus.DeadLetter)
                            .SetProperty(m => m.LockedBy, (string?)null)
                            .SetProperty(m => m.LockedUntil, (DateTime?)null),
                            cancellationToken);
                }
```

Son olarak `if (outcomes.Count == 0) return 0;` satırını şununla değiştir (aksi hâlde yalnızca exhausted mesaj varken PHASE 3 hiç çalışmaz):

```csharp
            if (outcomes.Count == 0 && exhausted.Count == 0) return 0;
```

ve metodun `return outcomes.Count;` satırını şununla değiştir:

```csharp
            return outcomes.Count + exhausted.Count;
```

- [ ] **Step 4: Derle ve tüm outbox testlerini çalıştır**

```bash
dotnet build framework/src/BBT.Aether.Infrastructure
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~Outbox"
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~NpgsqlLeaseStoreTests"
```

Beklenen: hepsi PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs \
        framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs
git commit -m "fix(outbox): dead-letter reclaimed messages whose retry budget is exhausted

Prevents an infinitely reclaimed message when a worker crash-loops between
lease and outcome-write."
```

---

## Task 3: Süresi dolmuş lease'i geri al — Inbox

**Neden:** `NpgsqlInboxLeaseStore` birebir aynı hatayı taşıyor (`WHERE "Status" = @pending`, `Processing` satırları hiç geri alınmıyor). Aynı düzeltme, `NextRetryAt` yerine `NextRetryTime` alan adıyla.

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs:51-70`
- Create: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlInboxLeaseStoreTests.cs`

- [ ] **Step 1: Inbox lease test dosyasını oluştur**

`framework/test/BBT.Aether.Postgres.Tests/NpgsqlInboxLeaseStoreTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using InboxMessage = BBT.Aether.Domain.Events.InboxMessage;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

[Collection("postgres")]
public sealed class NpgsqlInboxLeaseStoreTests(PostgresFixture fx)
{
    private readonly string _schema = "inbox_lease_test_" + Guid.NewGuid().ToString("N");

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options), IHasEfCoreInbox
    {
        public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureInbox();
        }
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        services.AddAetherNpgsql<TestDbContext>(fx.ConnectionString, SchemaSwitchingMode.QualifiedNames);
        services.AddAetherInbox<TestDbContext>(options => options.Schema = _schema);
        services.AddSingleton<IEventSerializer, SystemTextJsonEventSerializer>();
        return services.BuildServiceProvider();
    }

    private async Task SetupSchemaAsync(IServiceProvider sp)
    {
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA \"{_schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var configurator = sp.GetRequiredService<IAetherDbContextConfigurator<TestDbContext>>();
        await using var modelConn = new NpgsqlConnection(fx.ConnectionString);
        await modelConn.OpenAsync();
        await using var ctx = ActivatorUtilities.CreateInstance<TestDbContext>(
            sp, configurator.BuildOptions(modelConn, _schema, new SchemaScopeState()));
        var script = ctx.Database.GenerateCreateScript()
            .Replace(AetherSchemaModel.QuotedPlaceholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace(AetherSchemaModel.Placeholder, $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace($"CREATE SCHEMA \"{_schema}\";",
                     $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\";", StringComparison.Ordinal);

        await using var ddlConn = new NpgsqlConnection(fx.ConnectionString);
        await ddlConn.OpenAsync();
        await using (var setCmd = ddlConn.CreateCommand())
        {
            setCmd.CommandText = $"SET search_path TO \"{_schema}\";";
            await setCmd.ExecuteNonQueryAsync();
        }
        await using (var ddlCmd = ddlConn.CreateCommand())
        {
            ddlCmd.CommandText = script;
            await ddlCmd.ExecuteNonQueryAsync();
        }
    }

    private async Task InsertPendingMessageAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

        using (currentSchema.Change(_schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            await inboxStore.StoreAsync(new CloudEventEnvelope
            {
                Id = Guid.NewGuid().ToString(),
                Type = "TestEvent",
                Topic = "test-topic",
                Data = System.Text.Encoding.UTF8.GetBytes("{}")
            });

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task LeaseBatch_reclaims_processing_message_with_expired_lease()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'crashed-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' - interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.InboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
            leased[0].LockedBy.ShouldBe("worker-2");
            leased[0].RetryCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task LeaseBatch_does_not_reclaim_processing_message_with_valid_lease()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."InboxMessages"
                SET "Status" = 1,
                    "LockedBy" = 'healthy-worker',
                    "LockedUntil" = now() AT TIME ZONE 'utc' + interval '5 minutes'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IInboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.InboxMessage> leased;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                leased = await leaseStore.LeaseBatchAsync(10, "worker-2", TimeSpan.FromSeconds(30));
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(0);
        }
    }
}
```

> Kullanılan tiplerin tamamı doğrulandı: `IHasEfCoreInbox` (`BBT.Aether.Persistence`), `ConfigureInbox()` (`BBT.Aether.Domain.EntityFrameworkCore.Modeling`), `AddAetherInbox<T>`, `IInboxLeaseStore`, `IInboxStore` (`BBT.Aether.Events`) ve `BBT.Aether.Domain.Events.InboxMessage` mevcut. Inbox tablosunda retry alanının adı `NextRetryTime`, işlenme zamanı `HandledTime`.

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~NpgsqlInboxLeaseStoreTests.LeaseBatch_reclaims_processing_message_with_expired_lease"
```

Beklenen: FAIL — `leased.Count` 0 gelir.

- [ ] **Step 3: Inbox lease SQL'ini düzelt**

`NpgsqlInboxLeaseStore.cs` içinde `command.CommandText` atamasını şununla değiştir:

```csharp
        command.CommandText = $"""
            UPDATE {fullTableName}
            SET
                "RetryCount"  = CASE WHEN "Status" = @processing
                                     THEN "RetryCount" + 1
                                     ELSE "RetryCount" END,
                "Status"      = @processing,
                "LockedBy"    = @workerId,
                "LockedUntil" = @lockedUntil
            WHERE "Id" IN (
                SELECT "Id"
                FROM {fullTableName}
                WHERE "Status" IN (@pending, @processing)
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
```

Birleşik `Status IN (...)` formunun gerekçesi T1 Step 3'teki ile aynı: ayrı dallar
`Processing` + `LockedUntil IS NULL` satırını strand eder. `InboxMessages` için de
`LockedUntil IS NULL` senaryosunu kapsayan bir test ekle — T1'deki
`LeaseBatch_reclaims_processing_message_with_null_lock_expiry` testinin inbox karşılığı
(`"InboxMessages"` tablosu, `RetryCount` 1 beklenir).

`framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IInboxLeaseStore.cs` içindeki
`LeaseBatchAsync` özetine de aynı `<remarks>` eklenir.

- [ ] **Step 4: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~NpgsqlInboxLeaseStoreTests"
```

Beklenen: 2 test PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlInboxLeaseStore.cs \
        framework/test/BBT.Aether.Postgres.Tests/NpgsqlInboxLeaseStoreTests.cs
git commit -m "fix(inbox): reclaim expired leases stuck in Processing status

Same defect as the outbox lease store."
```

---

## Task 3b: Retry'ı tükenmiş inbox reclaim'lerini dead-letter'a düşür

> **Plana sonradan eklendi.** T3'ün kod incelemesinde ortaya çıktı ve iki bağımsız ajan
> tarafından **ship blocker** olarak nitelendi.

**Neden:** T3, inbox lease'ine reclaim-anında `RetryCount` artışı ekledi ama üst sınır
eklemedi — T2'nin outbox için kapattığı açığın aynısı. `InboxProcessor` bir mesajı leaseleyip
`ProcessSingleEventAsync`'e veriyor; worker o noktada sert çökerse (OOM kill, container
restart) `MarkAsFailedAsync` hiç çalışmıyor, satır `Processing`'de kalıyor, sonraki döngüde
reclaim ediliyor ve bu sonsuza kadar sürüyor. Inbox tarafında crash-loop yapan bir satırı
`DeadLetter`'a taşıyan **hiçbir yol yok**.

T3 yine de net iyileşme: öncesinde stranded satırlar hiç yeniden leaselenmiyordu (sessiz,
elle müdahale gerektiren kayıp). Ama bu guard olmadan production'a çıkmamalı.

**Not (düzeltme gerekmiyor):** `EfCoreInboxStore.MarkAsFailedAsync` mevcut `RetryCount`'u
okuyup artırdığı için, çökme + hata döngüsünde sayaç 2 artabilir. İncelemede bunun tek bir
hatanın iki kez sayılması değil, **iki gerçek başarısızlık** olduğu (biri sessiz çökme, biri
yakalanan hata) ve dead-letter'ı erkene çektiği için güvenli yönde olduğu teyit edildi.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlInboxLeaseStoreTests.cs`

**Uygulama şekli** — T2'nin outbox guard'ını (`98a8218`) yansıt, ama `InboxProcessor`'ın
yapısına uyarla: outbox'ta üç fazlı batch akışı var, inbox'ta `while` döngüsü içinde
mesaj-başına işleme var ve toplu outcome fazı yok.

`logger.LogInformation("Leased {Count} inbox events for worker {WorkerId}", ...)` satırından
hemen sonra bölme yapılır:

```csharp
                var exhausted = pendingEvents.Where(m => m.RetryCount > options.MaxRetryCount).ToList();
                var processable = pendingEvents.Where(m => m.RetryCount <= options.MaxRetryCount).ToList();
```

`exhausted` boş değilse yeni bir `BeginRequiresNew` UoW içinde toplu dead-letter yazılır
(`IAetherDbContextProvider<TDbContext>` scope'tan çözülür — `OutboxProcessor`'daki desenin
aynısı), `LockedBy == workerId` guard'ıyla:

```csharp
                        .SetProperty(m => m.Status, IncomingEventStatus.DeadLetter)
                        .SetProperty(m => m.LockedBy, (string?)null)
                        .SetProperty(m => m.LockedUntil, (DateTime?)null)
```

`foreach` döngüsü `pendingEvents` yerine `processable` üzerinde döner; `totalProcessed`
`exhausted.Count`'u da içerir (aksi hâlde yalnızca exhausted mesaj olan bir tur "iş yok"
sinyali verip adaptive polling'i yanıltır).

**Sonsuz döngü kontrolü:** `while` döngüsü `pendingEvents` boşalınca kırılıyor. Exhausted
satırlar dead-letter'a taşındığı için bir sonraki lease onları `Status IN (0,1)` filtresiyle
zaten almaz — dönmeyen bir tur oluşmaz.

**Testler** — `NpgsqlInboxLeaseStoreTests.cs` içine:
- Lease store'un `MaxRetryCount` filtrelemediğini sabitleyen sözleşme testi (T2'deki
  `LeaseBatch_still_reclaims_when_retry_count_at_max_so_processor_can_dead_letter`'ın inbox
  karşılığı).
- `InboxProcessor.RunAsync` sonrası, `RetryCount` bütçesi aşılmış bir satırın `DeadLetter`
  (Status 3) olduğunu ve `LockedBy`/`LockedUntil` alanlarının temizlendiğini doğrulayan test.

**Eşik tutarlılığı:** T2 ile aynı `> options.MaxRetryCount` eşiği kullanılır. Mevcut
`MarkAsFailedAsync` içindeki `RetryCount + 1 >= options.MaxRetryCount` **değiştirilmez** —
iki eşiğin uzlaştırılması ayrı bir iş kalemi olarak izleniyor.

---

## Task 4: Backoff — kısmi batch'te busy moda düşme

**Neden:** `processed > 0` olduğunda gecikme `BusyPollingInterval`'a (100 ms) sıfırlanıyor — batch 100'de 1 mesaj gelmiş olsa bile. 60 sn tavanına geri tırmanmak 10 poll / ~102 sn sürüyor. Preprod ölçümünde toplam poll'lerin ~%37'si bu tırmanmalar.

Ayrıca mevcut test (`AdaptivePollingTests`) mantığı **kopyalayıp** kendi kopyasını test ediyor — gerçek kodu değiştirsen bile geçer. Önce mantık paylaşılan saf bir fonksiyona çıkarılır.

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/AdaptivePolling.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs`

- [ ] **Step 1: Test dosyasını gerçek uygulamayı çağıracak şekilde yeniden yaz**

`OutboxBackgroundServiceTests.cs`'in **tamamını** şununla değiştir:

```csharp
using System;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class AdaptivePollingTests
{
    private static readonly AetherOutboxOptions Opts = new()
    {
        BatchSize           = 100,
        BusyPollingInterval = TimeSpan.FromMilliseconds(100),
        IdlePollingInterval = TimeSpan.FromSeconds(5),
        MaxPollingInterval  = TimeSpan.FromSeconds(60),
    };

    private static TimeSpan Next(TimeSpan current, int processed) =>
        AdaptivePolling.NextDelay(
            current, processed, Opts.BatchSize,
            Opts.BusyPollingInterval, Opts.IdlePollingInterval, Opts.MaxPollingInterval);

    [Fact]
    public void Full_batch_returns_busy_interval()
    {
        Next(Opts.IdlePollingInterval, processed: 100).ShouldBe(Opts.BusyPollingInterval);
    }

    [Fact]
    public void Partial_batch_returns_idle_interval_not_busy()
    {
        // Kuyruk boşaldı: 100'lük batch'te 1 mesaj geldi. 100 ms'e düşmek 10 poll'lük
        // gereksiz tırmanma üretiyordu.
        Next(TimeSpan.FromSeconds(60), processed: 1).ShouldBe(Opts.IdlePollingInterval);
        Next(TimeSpan.FromSeconds(60), processed: 99).ShouldBe(Opts.IdlePollingInterval);
    }

    [Fact]
    public void Idle_doubles_delay_each_round()
    {
        var d1 = Next(Opts.IdlePollingInterval, processed: 0); // 10s
        var d2 = Next(d1, processed: 0);                       // 20s
        var d3 = Next(d2, processed: 0);                       // 40s
        var d4 = Next(d3, processed: 0);                       // 60s (capped)
        var d5 = Next(d4, processed: 0);                       // 60s (stays capped)

        d1.ShouldBe(TimeSpan.FromSeconds(10));
        d2.ShouldBe(TimeSpan.FromSeconds(20));
        d3.ShouldBe(TimeSpan.FromSeconds(40));
        d4.ShouldBe(TimeSpan.FromSeconds(60));
        d5.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Partial_batch_then_idle_climbs_from_idle_interval_not_from_busy()
    {
        var afterPartial = Next(TimeSpan.FromSeconds(60), processed: 3);
        var afterEmpty   = Next(afterPartial, processed: 0);

        afterPartial.ShouldBe(TimeSpan.FromSeconds(5));
        afterEmpty.ShouldBe(TimeSpan.FromSeconds(10));
    }
}
```

- [ ] **Step 2: Testi çalıştır, derlenmediğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~AdaptivePollingTests"
```

Beklenen: FAIL — derleme hatası `CS0103: The name 'AdaptivePolling' does not exist`.

- [ ] **Step 3: `AdaptivePolling`'i oluştur**

`framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/AdaptivePolling.cs`:

```csharp
using System;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Adaptive polling delay policy shared by the inbox and outbox dispatchers.
/// </summary>
/// <remarks>
/// A full batch means more work is almost certainly waiting, so poll again immediately.
/// A partial batch means the queue just drained — returning to the busy interval would
/// force ~10 wasted polls climbing back to the cap, which dominated the dispatcher's
/// database cost in production measurements.
/// </remarks>
public static class AdaptivePolling
{
    /// <summary>
    /// Computes the delay before the next dispatcher poll.
    /// </summary>
    /// <param name="current">The delay used before the poll that just completed.</param>
    /// <param name="processed">Number of messages handled by the poll that just completed.</param>
    /// <param name="batchSize">The configured lease batch size.</param>
    /// <param name="busyInterval">Delay to use when a full batch was returned.</param>
    /// <param name="idleInterval">Delay to use when a partial batch was returned.</param>
    /// <param name="maxInterval">Upper bound for the exponential idle backoff.</param>
    public static TimeSpan NextDelay(
        TimeSpan current,
        int processed,
        int batchSize,
        TimeSpan busyInterval,
        TimeSpan idleInterval,
        TimeSpan maxInterval)
    {
        if (processed >= batchSize && batchSize > 0) return busyInterval;
        if (processed > 0) return idleInterval;

        var next = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
        return next > maxInterval ? maxInterval : next;
    }
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~AdaptivePollingTests"
```

Beklenen: 4 test PASS.

- [ ] **Step 5: `OutboxBackgroundService`'i gerçek uygulamayı kullanacak şekilde değiştir**

`OutboxBackgroundService.cs` içinde `ExecuteAsync` gövdesindeki delay hesabını değiştir. `delay = processed > 0 ? ... : Min(...)` satırını şununla değiştir:

```csharp
                delay = AdaptivePolling.NextDelay(
                    delay, processed, options.BatchSize,
                    options.BusyPollingInterval, options.IdlePollingInterval, options.MaxPollingInterval);
```

Dosyanın sonundaki artık kullanılmayan `private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;` satırını sil.

- [ ] **Step 6: `InboxBackgroundService`'e aynı değişikliği uygula**

`InboxBackgroundService.cs` satır 23-25'teki şu ifadeyi:

```csharp
                delay = processed > 0
                    ? options.BusyPollingInterval
                    : Min(delay * 2, options.MaxPollingInterval);
```

şununla değiştir (Inbox'ta batch size özelliğinin adı `ProcessingBatchSize`):

```csharp
                delay = AdaptivePolling.NextDelay(
                    delay, processed, options.ProcessingBatchSize,
                    options.BusyPollingInterval, options.IdlePollingInterval, options.MaxPollingInterval);
```

Satır 41'deki artık kullanılmayan `private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;` satırını sil.

- [ ] **Step 7: Derle ve tüm testleri çalıştır**

```bash
dotnet build
dotnet test framework/test/BBT.Aether.Infrastructure.Tests
```

Beklenen: build 0 error, testler PASS.

- [ ] **Step 8: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/AdaptivePolling.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxBackgroundService.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxBackgroundServiceTests.cs
git commit -m "perf(inbox,outbox): do not drop to busy polling on a partial batch

A single message reset the delay to 100ms, costing ~10 polls to climb back to
the cap. Partial batches now return to the idle interval. Delay policy extracted
to AdaptivePolling so the test exercises the real implementation instead of a copy."
```

---

## Task 5: Outbox cleanup'ı aralığa bağla

**Neden:** `OutboxProcessor.RunAsync` her döngüde koşulsuz `CleanupProcessedMessagesAsync` çağırıyor. 0 mesaj leaseleyen boş poll bile ikinci bir `RequiresNew` transactional UoW açıyor — boş döngünün DB maliyeti iki katına çıkıyor. Inbox'ta bu config zaten var (`AetherInboxOptions.CleanupInterval`), Outbox'ta yok.

**Files:**
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxCleanupIntervalTests.cs` (yeni)

- [ ] **Step 1: Options'a alanları ekle**

`AetherOutboxOptions.cs` içine `RetentionPeriod` satırının altına ekle:

```csharp
    /// <summary>
    /// Minimum time between retention cleanup passes. Cleanup runs at most once per interval,
    /// not on every dispatcher poll.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of processed messages deleted per cleanup pass.</summary>
    public int CleanupBatchSize { get; set; } = 1000;
```

- [ ] **Step 2: Başarısız testi yaz**

`framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxCleanupIntervalTests.cs`:

```csharp
using System;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class OutboxCleanupIntervalTests
{
    [Fact]
    public void Options_expose_cleanup_interval_matching_inbox_defaults()
    {
        var outbox = new AetherOutboxOptions();
        var inbox = new AetherInboxOptions();

        outbox.CleanupInterval.ShouldBe(inbox.CleanupInterval);
        outbox.CleanupBatchSize.ShouldBe(inbox.CleanupBatchSize);
    }

    [Theory]
    // (son cleanup üzerinden geçen süre, aralık, çalışmalı mı)
    [InlineData(0, 60, false)]
    [InlineData(59, 60, false)]
    [InlineData(60, 60, true)]
    [InlineData(3600, 60, true)]
    public void IsCleanupDue_respects_interval(int elapsedMinutes, int intervalMinutes, bool expected)
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var lastRun = now.AddMinutes(-elapsedMinutes);

        CleanupSchedule
            .IsDue(lastRun, now, TimeSpan.FromMinutes(intervalMinutes))
            .ShouldBe(expected);
    }

    [Fact]
    public void IsCleanupDue_is_true_on_first_run()
    {
        CleanupSchedule
            .IsDue(DateTime.MinValue, DateTime.UtcNow, TimeSpan.FromHours(1))
            .ShouldBeTrue();
    }
}
```

- [ ] **Step 3: Testi çalıştır, derlenmediğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxCleanupIntervalTests"
```

Beklenen: FAIL — `CS0103: The name 'CleanupSchedule' does not exist`.

- [ ] **Step 4: `CleanupSchedule`'ı oluştur**

`framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/CleanupSchedule.cs`:

```csharp
using System;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Decides whether a retention cleanup pass is due, so cleanup runs on an interval
/// rather than on every dispatcher poll.
/// </summary>
public static class CleanupSchedule
{
    /// <summary>
    /// Returns true when at least <paramref name="interval"/> has elapsed since
    /// <paramref name="lastRunUtc"/>. Always true on the first run
    /// (<see cref="DateTime.MinValue"/>).
    /// </summary>
    public static bool IsDue(DateTime lastRunUtc, DateTime nowUtc, TimeSpan interval)
        => nowUtc - lastRunUtc >= interval;
}
```

- [ ] **Step 5: Testi çalıştır, geçtiğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxCleanupIntervalTests"
```

Beklenen: 6 test PASS.

- [ ] **Step 6: Processor'ı gating kullanacak şekilde değiştir**

`OutboxProcessor.cs` sınıf gövdesinin en üstüne (ilk metottan önce) alan ekle:

```csharp
    private DateTime _lastCleanupUtc = DateTime.MinValue;
```

`CleanupProcessedMessagesAsync` metodunun ilk satırı olan
`if (string.IsNullOrWhiteSpace(options.Schema)) return;` ifadesinin **altına** ekle:

```csharp
        var now = clock.UtcNow;
        if (!CleanupSchedule.IsDue(_lastCleanupUtc, now, options.CleanupInterval)) return;
        _lastCleanupUtc = now;
```

Aynı metotta `.Take(options.BatchSize)` ifadesini şununla değiştir:

```csharp
                .Take(options.CleanupBatchSize)
```

> `OutboxProcessor` DI'da **singleton** (`AetherOutboxServiceCollectionExtensions.AddAetherOutbox` → `services.AddSingleton<IOutboxProcessor, ...>`), bu yüzden `_lastCleanupUtc` alan durumu poll'ler arasında korunur.

- [ ] **Step 7: Derle ve testleri çalıştır**

```bash
dotnet build
dotnet test framework/test/BBT.Aether.Infrastructure.Tests
```

Beklenen: build 0 error, testler PASS.

- [ ] **Step 8: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/CleanupSchedule.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxCleanupIntervalTests.cs
git commit -m "perf(outbox): run retention cleanup on an interval, not every poll

Cleanup opened a second RequiresNew transaction on every dispatcher poll,
doubling the database cost of an idle cycle. Adds CleanupInterval and
CleanupBatchSize, matching the inbox options."
```

---

## Task 6: Inbox cleanup aralığını gerçekten uygula

**Neden:** `AetherInboxOptions.CleanupInterval` **var** ve appsettings'te set edilmiş, ama `InboxProcessor.RunAsync` (satır 29) `CleanupOldMessagesAsync`'i koşulsuz çağırıyor — config hiçbir şey yapmıyor.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxCleanupIntervalTests.cs` (ekleme)

- [ ] **Step 1: Testi ekle**

`OutboxCleanupIntervalTests.cs` sınıfına ekle:

```csharp
    [Fact]
    public void Inbox_and_outbox_share_the_same_cleanup_schedule_policy()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var interval = TimeSpan.FromHours(1);

        CleanupSchedule.IsDue(now.AddMinutes(-30), now, interval).ShouldBeFalse();
        CleanupSchedule.IsDue(now.AddMinutes(-90), now, interval).ShouldBeTrue();
    }
```

- [ ] **Step 2: Testi çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~OutboxCleanupIntervalTests"
```

Beklenen: PASS (T5'in `CleanupSchedule`'ı zaten var).

- [ ] **Step 3: `InboxProcessor`'a `IClock` bağımlılığını ekle**

`InboxProcessor`'ın bugün `IClock`'u **yok** (doğrulandı). Birincil kurucuyu (satır 16-20) şununla değiştir:

```csharp
public class InboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity workerIdentity,
    IClock clock,
    ILogger<InboxProcessor<TDbContext>> logger,
    AetherInboxOptions options) : IInboxProcessor
```

Dosyanın başına `using BBT.Aether.Clock;` ekle (yoksa).

- [ ] **Step 4: Gating'i ekle**

`InboxProcessor.cs` sınıf gövdesinin en üstüne (`RunAsync`'ten önce) alan ekle:

```csharp
    private DateTime _lastCleanupUtc = DateTime.MinValue;
```

`CleanupOldMessagesAsync` metodunun gövdesinin **en başına** ekle:

```csharp
        var nowUtc = clock.UtcNow;
        if (!CleanupSchedule.IsDue(_lastCleanupUtc, nowUtc, options.CleanupInterval)) return;
        _lastCleanupUtc = nowUtc;
```

> `AddAetherInbox` `IInboxProcessor`'ı singleton kaydediyor (`services.AddSingleton<IInboxProcessor, InboxProcessor<TDbContext>>()`), bu yüzden alan durumu poll'ler arasında korunur. `IClock` Aether core'da kayıtlı olduğundan DI değişikliği gerekmez.

- [ ] **Step 5: Derle ve testleri çalıştır**

```bash
dotnet build
dotnet test framework/test/BBT.Aether.Infrastructure.Tests
```

Beklenen: build 0 error, testler PASS.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/InboxProcessor.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/OutboxCleanupIntervalTests.cs
git commit -m "fix(inbox): honour CleanupInterval instead of cleaning on every poll

AetherInboxOptions.CleanupInterval was configured but never read."
```

---

## Task 7: Cleanup'ı `ExecuteDeleteAsync`'e çevir

**Neden:** Hem outbox hem inbox cleanup, silinecek satırları `ToListAsync()` ile belleğe yükleyip change tracker'a alıyor. Tek `DELETE` yeterli.

**Ek kapsam — cleanup jitter (T5 review'ından geldi).** `_lastCleanupUtc` alanı
`DateTime.MinValue` ile başlıyor, yani `IsDue` ilk poll'de her zaman true. Deployment sırasında
~30 outbox pod'u aynı anda ayağa kalktığında hepsi ilk cleanup'ı eş zamanlı çalıştırır ve
aynı tabloya aynı anda 1000'er satırlık silme gönderir. T5'te bloklayıcı sayılmadı çünkü
önceki davranış (her pod, her poll) zaten daha kötüydü — ama `ExecuteDelete`'e geçerken
birlikte kapatılmalı.

Çözüm: `_lastCleanupUtc`'yi sıfırdan başlatmak yerine, kurulumda `CleanupInterval` aralığı
içinde rastgele bir noktaya seed et; böylece ilk cleanup'lar aralığa yayılır. Aynısı
`InboxProcessor` için de yapılır. Rastgelelik test edilebilirliği bozmamalı — seed'i
enjekte edilebilir tut ya da testlerde `CleanupInterval = TimeSpan.Zero` ile etkisiz kıl.

**Files:**
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs:120-141`
- Test: `framework/test/BBT.Aether.Postgres.Tests/OutboxCleanupTests.cs` (yeni)

- [ ] **Step 1: Entegrasyon testini yaz**

`framework/test/BBT.Aether.Postgres.Tests/OutboxCleanupTests.cs` dosyasını oluştur. `NpgsqlLeaseStoreTests.cs`'teki `TestDbContext`, `BuildProvider`, `SetupSchemaAsync`, `InsertPendingMessageAsync` yardımcılarını **birebir kopyala** (aynı `_schema` deseniyle, `lease_test_` yerine `cleanup_test_` ön ekiyle), sonra şu testi ekle:

```csharp
    [Fact]
    public async Task Cleanup_deletes_processed_messages_older_than_retention()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        // Mesajı 10 gün önce işlenmiş yap
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 2,
                    "ProcessedAt" = now() AT TIME ZONE 'utc' - interval '10 days'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var processor = sp.GetRequiredService<IOutboxProcessor>();
        await processor.RunAsync();

        await using var conn2 = new NpgsqlConnection(fx.ConnectionString);
        await conn2.OpenAsync();
        await using var countCmd = conn2.CreateCommand();
        countCmd.CommandText = $"SELECT count(*) FROM \"{_schema}\".\"OutboxMessages\"";
        var remaining = (long)(await countCmd.ExecuteScalarAsync())!;

        remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_keeps_processed_messages_inside_retention()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages"
                SET "Status" = 2,
                    "ProcessedAt" = now() AT TIME ZONE 'utc' - interval '1 hour'
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var processor = sp.GetRequiredService<IOutboxProcessor>();
        await processor.RunAsync();

        await using var conn2 = new NpgsqlConnection(fx.ConnectionString);
        await conn2.OpenAsync();
        await using var countCmd = conn2.CreateCommand();
        countCmd.CommandText = $"SELECT count(*) FROM \"{_schema}\".\"OutboxMessages\"";
        var remaining = (long)(await countCmd.ExecuteScalarAsync())!;

        remaining.ShouldBe(1);
    }
```

- [ ] **Step 2: Testleri çalıştır (mevcut `ToList`+`RemoveRange` ile geçmeli)**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~OutboxCleanupTests"
```

Beklenen: 2 test PASS. Bu, davranışı `ExecuteDelete`'e geçmeden **önce** sabitler.

- [ ] **Step 3: Outbox cleanup'ı `ExecuteDeleteAsync`'e çevir**

`OutboxProcessor.CleanupProcessedMessagesAsync` içindeki `var processed = await dbContext.OutboxMessages ... ToListAsync(...)` ve ardından gelen `if (processed.Count > 0) { ... RemoveRange(processed); }` bloğunun tamamını şununla değiştir:

```csharp
            var deleted = await dbContext.OutboxMessages
                .Where(m => m.Status == OutboxMessageStatus.Processed
                         && m.ProcessedAt != null
                         && m.ProcessedAt < cutoffDate)
                .OrderBy(m => m.ProcessedAt)
                .Take(options.CleanupBatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
                logger.LogInformation("Cleaned up {Count} processed outbox messages", deleted);
```

- [ ] **Step 4: Inbox cleanup'ı `ExecuteDeleteAsync`'e çevir**

`EfCoreInboxStore.CleanupOldMessagesAsync` gövdesindeki `var oldMessages = ...ToListAsync(...)` satırından metodun sonuna kadar olan kısmı şununla değiştir:

```csharp
        return await dbContext.InboxMessages
            .Where(m => m.Status == IncomingEventStatus.Processed &&
                        m.HandledTime != null &&
                        m.HandledTime < cutoffDate)
            .OrderBy(m => m.HandledTime)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);
```

- [ ] **Step 5: Testleri çalıştır — çeviri hatası olmadığını doğrula**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~OutboxCleanupTests"
dotnet test framework/test/BBT.Aether.Postgres.Tests
```

Beklenen: hepsi PASS.

> `ExecuteDeleteAsync` + `Take()` Npgsql 10'da `DELETE ... WHERE ctid IN (SELECT ... LIMIT n)` üretir. Test `InvalidOperationException: ... could not be translated` verirse `Take()`/`OrderBy()`'ı kaldırıp `.Where(...)` yüklemine `m.ProcessedAt < cutoffDate` sınırını tek başına bırak ve `CleanupInterval` gating'i (T5) parti boyutunu zaten saatte bire indirdiği için bu kabul edilebilir.

- [ ] **Step 6: Commit**

```bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs \
        framework/test/BBT.Aether.Postgres.Tests/OutboxCleanupTests.cs
git commit -m "perf(inbox,outbox): delete expired messages with ExecuteDelete

Avoids loading up to CleanupBatchSize tracked entities per cleanup pass."
```

---

## Task 8: `PartitionId` kolonu ve partial index

**Neden:** Kolonu tablolar küçükken eklemek gerekiyor; IDM hacmi geldikten sonra sıcak tabloda backfill kat kat pahalı (spec §0). Partial index ise poll maliyetini tablo boyutundan bağımsız kılıyor: mevcut `IX_OutboxMessages_Processing` partial değil, 7 günlük retention ile `Processed` satırlarını da indeksliyor.

Bu görevde kolon **yalnızca tanımlanır**; doldurma T9'da, sorguda kullanımı Faz 3'te.

**Files:**
- Modify: `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Events/OutboxMessage.cs`
- Modify: `framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Events/InboxMessage.cs`
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxMessage.cs`
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/InboxMessage.cs`
- Modify: `.../Modeling/OutboxModelBuilderExtensions.cs`
- Modify: `.../Modeling/InboxModelBuilderExtensions.cs`

- [ ] **Step 1: Entity ve DTO'lara özelliği ekle**

Dört dosyanın her birine, `RetryCount` özelliğinin altına ekle:

```csharp
    /// <summary>
    /// Logical partition this message belongs to, derived from the event subject.
    /// Written from day one so the dispatcher can lease partition-disjoint batches later
    /// without a backfill. Not read by any query yet.
    /// </summary>
    public short PartitionId { get; set; }
```

- [ ] **Step 2: `OutboxModelBuilderExtensions`'da kolonu ve partial index'i yapılandır**

`ConfigureOutbox` içinde, `entity.Property(e => e.LockedUntil);` satırının altına ekle:

```csharp
            entity.Property(e => e.PartitionId)
                .IsRequired()
                .HasDefaultValue((short)0);
```

Ardından mevcut iki `HasIndex` çağrısını şununla değiştir:

```csharp
            // Dispatch index: partial on the statuses the lease query can match, so its size
            // tracks outstanding work rather than table size. PartitionId leads the key so the
            // partition-filtered lease (phase 3) is a prefix scan.
            entity.HasIndex(e => new { e.PartitionId, e.NextRetryAt, e.CreatedAt })
                .HasDatabaseName("IX_OutboxMessages_Dispatch")
                .IncludeProperties(e => new { e.LockedUntil })
                .HasFilter("\"Status\" IN (0, 1)");

            // Index for cleanup of old processed messages
            entity.HasIndex(e => new { e.ProcessedAt })
                .HasDatabaseName("IX_OutboxMessages_Retention")
                .HasFilter("\"Status\" = 2");
```

- [ ] **Step 3: `InboxModelBuilderExtensions`'a aynısını uygula**

`ConfigureInbox` içinde satır 57'deki `entity.Property(e => e.LockedUntil);` ifadesinin altına ekle:

```csharp
            entity.Property(e => e.PartitionId)
                .IsRequired()
                .HasDefaultValue((short)0);
```

Ardından satır 60-65'teki mevcut iki index tanımını:

```csharp
            entity.HasIndex(e => new { e.Status, e.LockedUntil, e.NextRetryTime, e.CreatedAt })
                .HasDatabaseName("IX_InboxMessages_Processing");

            entity.HasIndex(e => new { e.Status, e.HandledTime })
                .HasDatabaseName("IX_InboxMessages_Retention");
```

şununla değiştir:

```csharp
            entity.HasIndex(e => new { e.PartitionId, e.NextRetryTime, e.CreatedAt })
                .HasDatabaseName("IX_InboxMessages_Dispatch")
                .IncludeProperties(e => new { e.LockedUntil })
                .HasFilter("\"Status\" IN (0, 1)");

            entity.HasIndex(e => new { e.HandledTime })
                .HasDatabaseName("IX_InboxMessages_Retention")
                .HasFilter("\"Status\" = 2");
```

> `IncludeProperties`'e `Id` konulmaz — `Id` primary key olduğu için Npgsql onu zaten leaf'te taşır ve açıkça eklemek `InvalidOperationException` üretir. Aynı gerekçeyle Step 2'deki outbox `IncludeProperties` ifadesinden de `e.Id`'yi çıkar; yalnızca `e.LockedUntil` kalsın.

- [ ] **Step 4: Derle ve tüm Postgres testlerini çalıştır**

Postgres testleri şemayı `GenerateCreateScript()` ile kuruyor, yani yeni kolon ve index'ler orada da oluşacak — bu, model yapılandırmasının geçerliliğinin kanıtı.

```bash
dotnet build
dotnet test framework/test/BBT.Aether.Postgres.Tests
```

Beklenen: build 0 error, tüm testler PASS.

- [ ] **Step 4b: Partial index'in gerçekten kullanıldığını `EXPLAIN` ile doğrula**

T1 review'ından gelen uyarı: PostgreSQL'in partial-index çıkarımı (`WHERE "Status" IN (0,1)`
predicate'inin sorguyu kapsadığını ispatlaması) yalnızca planner `@pending`/`@processing`
parametrelerini **plan zamanında sabit** olarak görürse çalışır (custom plan). Opak bind
parametresi olarak görürse (generic plan) çıkarım ispatlanamaz ve planner tam index'e ya da
seq scan'e dönebilir.

Bugün güvendeyiz: `NpgsqlOutboxLeaseStore` komutu ad-hoc gönderiyor (`.Prepare()` yok) ve kod
tabanında `Max Auto Prepare` konfigüre edilmemiş → custom plan. Yine de doğrula:

```sql
EXPLAIN (ANALYZE, BUFFERS)
UPDATE sys_queues."OutboxMessages" SET "Status" = 1
WHERE "Id" IN (
    SELECT "Id" FROM sys_queues."OutboxMessages"
    WHERE "Status" IN (0, 1)
      AND ("LockedUntil" IS NULL OR "LockedUntil" < now())
      AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= now())
    ORDER BY "CreatedAt" LIMIT 100 FOR UPDATE SKIP LOCKED);
```

Beklenen: plan `IX_OutboxMessages_Dispatch` kullanır (`Seq Scan` **değil**).

> **Kalıcı kısıt:** Bu komut için Npgsql auto-prepare açılmamalı; açılırsa plan davranışı
> yeniden ölçülmeli.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Events/OutboxMessage.cs \
        framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Events/InboxMessage.cs \
        framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/OutboxMessage.cs \
        framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/InboxMessage.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/Modeling/OutboxModelBuilderExtensions.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Domain/EntityFrameworkCore/Modeling/InboxModelBuilderExtensions.cs
git commit -m "feat(inbox,outbox): add PartitionId column and partial dispatch index

PartitionId is populated but not yet queried; adding it now avoids a backfill on
a hot table later. The dispatch index becomes partial on Status IN (0,1) so its
size tracks outstanding work rather than table size."
```

---

## Task 9: `PartitionId` yazma yolu (deterministic hash)

**Neden:** Partition key `envelope.Subject ?? envelope.Id`. vNext'in 10 event kontratının 10'unda da `[EventSubject]` var ve `DistributedEventBusBase` bunu envelope'a taşıyor — yani `Subject` bugün instance id ile dolu. `Subject`'in hiçbir yerde tekillik semantiği yok (inbox dedup `Id`'ye bakıyor), aggregate düzeyinde tekrar etmesi tasarım gereği.

`string.GetHashCode()` **kullanılmaz** — process'ler arası kararsız.

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/MessagePartitionResolver.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherInboxOptions.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs`
- Modify: `Directory.Packages.props`, `framework/src/BBT.Aether.Core/BBT.Aether.Core.csproj`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/MessagePartitionResolverTests.cs` (yeni)

- [ ] **Step 1: `System.IO.Hashing` paketini ekle**

`Directory.Packages.props` içindeki `<ItemGroup>` bloğuna alfabetik sırada ekle:

```xml
    <PackageVersion Include="System.IO.Hashing" Version="10.0.0" />
```

`framework/src/BBT.Aether.Core/BBT.Aether.Core.csproj` içindeki `<ItemGroup>` bloğuna ekle:

```xml
    <PackageReference Include="System.IO.Hashing" />
```

- [ ] **Step 2: Başarısız testi yaz**

`framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/MessagePartitionResolverTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Events;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events;

public sealed class MessagePartitionResolverTests
{
    private const int PartitionCount = 64;

    [Fact]
    public void Same_key_always_resolves_to_the_same_partition()
    {
        const string key = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        var first = MessagePartitionResolver.Resolve(key, PartitionCount);
        var second = MessagePartitionResolver.Resolve(key, PartitionCount);

        first.ShouldBe(second);
    }

    [Fact]
    public void Resolved_partition_is_within_range()
    {
        for (var i = 0; i < 1000; i++)
        {
            var p = MessagePartitionResolver.Resolve(Guid.NewGuid().ToString(), PartitionCount);
            p.ShouldBeGreaterThanOrEqualTo((short)0);
            p.ShouldBeLessThan((short)PartitionCount);
        }
    }

    [Fact]
    public void Distribution_across_partitions_is_reasonably_even()
    {
        var counts = new int[PartitionCount];
        const int samples = 64_000;

        for (var i = 0; i < samples; i++)
            counts[MessagePartitionResolver.Resolve(Guid.NewGuid().ToString(), PartitionCount)]++;

        var expected = samples / (double)PartitionCount;   // 1000
        counts.Min().ShouldBeGreaterThan((int)(expected * 0.8));
        counts.Max().ShouldBeLessThan((int)(expected * 1.2));
    }

    [Fact]
    public void Hash_is_stable_across_runs()
    {
        // Regression guard: partition algorithm is an architectural contract.
        // Changing it redistributes every existing row, so these values must not drift.
        MessagePartitionResolver.Resolve("instance-a", 64)
            .ShouldBe(MessagePartitionResolver.Resolve("instance-a", 64));
        MessagePartitionResolver.Resolve("instance-a", 64)
            .ShouldNotBe(MessagePartitionResolver.Resolve("instance-b", 64));
    }

    [Fact]
    public void Null_or_whitespace_key_resolves_to_partition_zero()
    {
        MessagePartitionResolver.Resolve(null, PartitionCount).ShouldBe((short)0);
        MessagePartitionResolver.Resolve("", PartitionCount).ShouldBe((short)0);
        MessagePartitionResolver.Resolve("   ", PartitionCount).ShouldBe((short)0);
    }
}
```

- [ ] **Step 3: Testi çalıştır, derlenmediğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~MessagePartitionResolverTests"
```

Beklenen: FAIL — `CS0103: The name 'MessagePartitionResolver' does not exist`.

- [ ] **Step 4: Resolver'ı oluştur**

`framework/src/BBT.Aether.Core/BBT/Aether/Events/MessagePartitionResolver.cs`:

```csharp
using System;
using System.IO.Hashing;
using System.Text;

namespace BBT.Aether.Events;

/// <summary>
/// Maps a message partition key to a logical partition using a deterministic hash.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is an architectural contract: changing it redistributes every existing
/// row across partitions. It is versioned as <c>xxhash64-mod</c> / <c>partitionVersion 1</c>.
/// </para>
/// <para>
/// <see cref="string.GetHashCode()"/> must never be used here — it is not stable across
/// processes or runtime versions.
/// </para>
/// </remarks>
public static class MessagePartitionResolver
{
    /// <summary>The partition algorithm identifier, for documentation and diagnostics.</summary>
    public const string Algorithm = "xxhash64-mod";

    /// <summary>The partition algorithm version. Bumping this requires a migration plan.</summary>
    public const int Version = 1;

    /// <summary>
    /// Resolves the logical partition for <paramref name="partitionKey"/>.
    /// Returns 0 when the key is null or blank.
    /// </summary>
    public static short Resolve(string? partitionKey, int partitionCount)
    {
        if (partitionCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(partitionCount), partitionCount, "Partition count must be positive.");

        if (string.IsNullOrWhiteSpace(partitionKey)) return 0;

        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(partitionKey));
        return (short)(hash % (ulong)partitionCount);
    }
}
```

- [ ] **Step 5: Testi çalıştır, geçtiğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~MessagePartitionResolverTests"
```

Beklenen: 5 test PASS.

- [ ] **Step 6: Options'a `PartitionCount` ekle**

`AetherOutboxOptions.cs` **ve** `AetherInboxOptions.cs`'in her ikisine ekle:

```csharp
    /// <summary>
    /// Number of logical partitions messages are hashed into.
    /// <para>
    /// This is NOT a runtime knob. Changing it re-maps every key and requires a migration
    /// plan; existing rows keep their old partition. Algorithm and version are recorded on
    /// <see cref="MessagePartitionResolver"/>.
    /// </para>
    /// </summary>
    public int PartitionCount { get; set; } = 64;
```

- [ ] **Step 7: Yazma yolunu bağla**

`EfCoreOutboxStore.StoreAsync` içinde, `var outboxMessage = new Domain.Events.OutboxMessage(...)` nesne başlatıcısına `RetryCount = 0,` satırının altına ekle:

```csharp
            PartitionId = MessagePartitionResolver.Resolve(
                envelope.Subject ?? envelope.Id, options.PartitionCount),
```

`EfCoreInboxStore.cs` satır 65'teki `var inboxMessage = new Domain.Events.InboxMessage(envelope.Id, envelope.Type, serializedBytes)` nesne başlatıcısına aynı satırı ekle:

```csharp
            PartitionId = MessagePartitionResolver.Resolve(
                envelope.Subject ?? envelope.Id, options.PartitionCount),
```

`EfCoreInboxStore` kurucusunda `AetherInboxOptions options` **zaten var** (satır 20), ek DI değişikliği gerekmez.

- [ ] **Step 8: Derle ve tüm testleri çalıştır**

```bash
dotnet build
dotnet test framework/test/BBT.Aether.Infrastructure.Tests
dotnet test framework/test/BBT.Aether.Postgres.Tests
```

Beklenen: build 0 error, tüm testler PASS.

- [ ] **Step 9: Commit**

```bash
git add Directory.Packages.props \
        framework/src/BBT.Aether.Core/BBT.Aether.Core.csproj \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/MessagePartitionResolver.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherInboxOptions.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreOutboxStore.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/EfCoreInboxStore.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/MessagePartitionResolverTests.cs
git commit -m "feat(inbox,outbox): populate PartitionId from the event subject

Partition key is envelope.Subject (already the instance id via [EventSubject])
falling back to envelope.Id. Uses xxHash64 — string.GetHashCode is not stable
across processes. Nothing reads the column yet."
```

---

## Task 10: vNext — EF migration

**Neden:** `OutboxMessages`/`InboxMessages` tabloları vNext'in `MessagingDbContext`'i tarafından yönetiliyor; Aether yalnızca model yapılandırmasını sağlıyor. T8/T9'daki model değişikliği bir migration gerektirir.

**Files:**
- Create: `src/BBT.Workflow.Infrastructure/Migrations/MessagingDb/<timestamp>_AddPartitionIdAndPartialDispatchIndex.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Migrations/MessagingDb/MessagingDbContextModelSnapshot.cs` (EF üretir)

- [ ] **Step 1: Aether'ı yerel olarak paketle ve vNext'in referansını güncelle**

vNext, Aether'ı NuGet paketi olarak tüketiyor: `Directory.Build.props:5` → `<AetherPackageVersion>1.0.33</AetherPackageVersion>`. Yerel doğrulama için ön sürüm paketle:

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
dotnet pack -c Release -o ./local-packages -p:Version=1.0.34-faz1
```

vNext `nuget.config`'inde `<packageSources>` bloğuna yerel kaynağı ekle:

```xml
    <add key="aether-local" value="/Users/U0B006/Documents/repos/burgan-tech/aether/local-packages" />
```

vNext `Directory.Build.props` satır 5'i güncelle:

```xml
        <AetherPackageVersion>1.0.34-faz1</AetherPackageVersion>
```

Restore et:

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet restore
```

> `nuget.config` ve `Directory.Build.props`'taki bu iki değişiklik **yerel doğrulama içindir, commit edilmez**. Aether resmî sürümü yayımlandığında yalnızca `AetherPackageVersion` gerçek sürüme yükseltilip commit edilir.

- [ ] **Step 2: Migration'ı üret**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet ef migrations add AddPartitionIdAndPartialDispatchIndex \
  --project src/BBT.Workflow.Infrastructure \
  --startup-project workers/BBT.Workflow.DbMigrator \
  --context MessagingDbContext \
  --output-dir Migrations/MessagingDb
```

Beklenen: `Migrations/MessagingDb/` altında yeni bir migration ve güncellenmiş snapshot.

- [ ] **Step 3: Üretilen migration'ı `CONCURRENTLY` için düzenle**

EF, index'i `CREATE INDEX` olarak üretir; bu, sıcak tabloyu yazmalara karşı kilitler. Üretilen migration'ın `Up` metodunda **her** `CreateIndex` çağrısını sil ve yerine ham SQL koy. `Up`'ın sonuna ekle:

```csharp
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OutboxMessages_Dispatch"
                ON sys_queues."OutboxMessages" ("PartitionId", "NextRetryAt", "CreatedAt")
                INCLUDE ("LockedUntil")
                WHERE "Status" IN (0, 1);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InboxMessages_Dispatch"
                ON sys_queues."InboxMessages" ("PartitionId", "NextRetryTime", "CreatedAt")
                INCLUDE ("LockedUntil")
                WHERE "Status" IN (0, 1);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Processing";
                """, suppressTransaction: true);
```

`Down` metoduna simetrik `DROP INDEX CONCURRENTLY IF EXISTS` ifadelerini ekle.

> `suppressTransaction: true` zorunlu — PostgreSQL `CREATE INDEX CONCURRENTLY`'yi transaction içinde çalıştırmaz.

**Index adları çakışmamalı.** Yeni retention index'i eski `IX_*_Cleanup` adını **yeniden
kullanmaz**, `IX_*_Retention` olur. Gerekçe review'da deneysel olarak ortaya çıktı: aynı adı
şekil değiştirerek yeniden kullanmak, `CREATE INDEX CONCURRENTLY IF NOT EXISTS`'in mevcut
index'e sessizce no-op yapmasına yol açıyordu — migration başarılı görünüp index eski şeklinde
kalıyordu. Ayrı ad, temp-index + `ALTER INDEX ... RENAME` dansını ve kısmi-retry boşluğunu
tamamen ortadan kaldırır: sekiz ad (`_Processing`×2, `_Cleanup`×2, `_Dispatch`×2,
`_Retention`×2) tümüyle ayrık olduğu için hiçbir adım o an hizmet veren bir index'i düşüremez.

**Retry self-healing.** Her `CREATE INDEX CONCURRENTLY` öncesine koşulsuz bir
`DROP INDEX CONCURRENTLY IF EXISTS` konur. Yarıda kalan bir `CONCURRENTLY` inşası geride
`INVALID` bir index bırakır ve `IF NOT EXISTS` onu "var" sayıp yeniden inşa etmez — guard her
denemede aynı noktada patlar, otomatik deploy pipeline'ı kalıcı olarak takılır (review'da
`indisvalid=false` yapılarak yeniden üretildi). Öndeki drop bu kalıntıyı temizler.

**Guard boş geçmemeli.** `pg_index` kontrolü yalnızca geçersiz satırları saymaz; dört index'in
de **var olduğunu ve geçerli olduğunu** doğrular (`count(*) = 4 AND indisvalid`). Aksi hâlde
index tamamen silinmişse guard hiç satır bulamayıp sessizce geçerdi.

> **Runbook notu (operasyon):** Öndeki koşulsuz drop yalnızca EF'in kendi history kontrolü
> devredeyken güvenlidir. Migration'ın SQL gövdesi **elle** yeniden çalıştırılırsa (EF geçmişi
> baypas edilerek), o an hizmet veren geçerli index düşürülüp yeniden inşa edilir — yani
> kaçınmaya çalıştığımız "index'siz pencere" açılır. EF'in otomatik tekrarı zaten iki
> `AddColumn` çağrısında duplicate-column hatasıyla durur, `--idempotent` script'ler de
> history satırıyla korunur. Elle çalıştırma yapılacaksa önce index durumu kontrol edilmeli.

- [ ] **Step 4: Migration'ı yerel Docker Postgres'e uygula**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext/etc/docker && ./run-docker.sh
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet run --project workers/BBT.Workflow.DbMigrator
```

Beklenen: migration hatasız uygulanır.

- [ ] **Step 5: Şemayı doğrula**

```bash
docker exec -i $(docker ps -qf name=postgres) psql -U postgres -d workflow -c '\d sys_queues."OutboxMessages"'
```

Beklenen çıktıda `PartitionId | smallint | not null | 0` satırı ve `"IX_OutboxMessages_Dispatch"` partial index'i (`WHERE (("Status" = ANY (ARRAY[0, 1])))`) görünür; `IX_OutboxMessages_Processing` **görünmez**.

- [ ] **Step 6: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
git checkout -b feature/outbox-faz1-migration
git add src/BBT.Workflow.Infrastructure/Migrations/MessagingDb/
git commit -m "feat(messaging): add PartitionId column and partial dispatch indexes

Indexes are created CONCURRENTLY outside a transaction so the migration does not
lock the outbox/inbox tables against writes."
```

---

## Task 11: vNext — worker konfigürasyonu

**Neden:** T5 yeni `CleanupInterval`/`CleanupBatchSize` alanlarını ekledi; outbox worker'ın appsettings'i bunları set etmiyor. Retention 7 gün → 2 gün, dispatch index partial olsa da tablo boyutunu ve cleanup maliyetini düşürür.

**Files:**
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json`
- Modify: `workers/BBT.Workflow.Workers.Inbox/appsettings.json`

- [ ] **Step 1: Outbox appsettings'i güncelle**

`workers/BBT.Workflow.Workers.Outbox/appsettings.json` içindeki `Aether.Outbox` bloğunu şununla değiştir:

```json
    "Outbox": {
      "Schema": "sys_queues",
      "BatchSize": 100,
      "PartitionCount": 64,
      "LeaseDuration": "00:00:30",
      "RetentionPeriod": "2.00:00:00",
      "CleanupInterval": "01:00:00",
      "CleanupBatchSize": 1000,
      "MaxRetryCount": 5,
      "RetryBaseDelay": "00:01:00",
      "BusyPollingInterval": "00:00:00.100",
      "IdlePollingInterval": "00:00:05",
      "MaxPollingInterval": "00:01:00"
    }
```

> `MaxPollingInterval` Faz 1'de **60 sn'de kalır**. 300 sn'ye çıkarmak sinyale bağlı (Faz 2) — sinyal olmadan yükseltmek yayınlama gecikmesini 5 dakikaya çıkarır.

- [ ] **Step 2: Inbox appsettings'i güncelle**

`workers/BBT.Workflow.Workers.Inbox/appsettings.json` içindeki `Aether.Inbox` bloğunda iki satırı değiştir/ekle:

```json
      "RetentionPeriod": "2.00:00:00",
      "PartitionCount": 64,
```

- [ ] **Step 3: Her iki worker'ı yerel olarak çalıştır ve temiz başladıklarını doğrula**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet run --project workers/BBT.Workflow.Workers.Outbox
```

Beklenen: exception yok; `Aether:Outbox` bağlama hatası yok. Ctrl+C ile durdur, aynısını Inbox worker için tekrarla.

- [ ] **Step 4: Commit**

```bash
git add workers/BBT.Workflow.Workers.Outbox/appsettings.json \
        workers/BBT.Workflow.Workers.Inbox/appsettings.json
git commit -m "chore(workers): configure outbox cleanup interval and shorten retention"
```

---

## Task 12: Bütünsel doğrulama

- [ ] **Step 1: Aether — tüm solution build + test**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
dotnet build
dotnet test
```

Beklenen: build 0 error; tüm testler PASS.

- [ ] **Step 2: vNext — build + test**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
dotnet build
dotnet test
```

Beklenen: build 0 error. **Not:** `Application.Tests`'te master'da zaten var olan hatalar mevcut ([[load-test-remediation]] — 24 pre-existing failure). Faz 1 öncesi/sonrası sayıyı karşılaştır; **artmamalı**.

Karşılaştırma için Faz 1'e başlamadan önce taban çizgisini al:

```bash
git stash && dotnet test 2>&1 | tail -5 && git stash pop
```

- [ ] **Step 3: Preprod doğrulama sorgusu (deploy sonrası)**

B1'in düzeldiğini kanıtlar — deploy öncesi ve 24 saat sonrası çalıştır:

```sql
SELECT count(*) AS stuck_processing
FROM sys_queues."OutboxMessages"
WHERE "Status" = 1 AND "LockedUntil" < now();
```

Beklenen: deploy öncesi > 0, 24 saat sonra **0**.

Ayrıca Elastic'te şu sayıyı karşılaştır: `"Error processing outbox messages"` — Faz 1 öncesinin **<%10'u** olmalı (bu hatalar DB connect timeout kaynaklı; azalma poll sayısının düşmesinden gelir).

---

## Bu Planın Kapsamı Dışında

| Konu | Nerede |
|---|---|
| Wake-up signal (publisher, coordinator, `[Topic]` endpoint, `MaxPollingInterval` 300 sn, replica 10 → 2–3) | **Faz 2 planı** (ayrı doküman) |
| Partition okuma yolu (lease'e `PartitionId = ANY(...)`, sinyal hedefleme) | **Faz 3 planı** (ayrı doküman) |
| Npgsql `Max Pool Size`, PgBouncer tuning, API pod bağlantı churn'ü | Spec §10 — ayrı iş kalemleri, bu plan pool sorununu çözmez |
| **Hata durumunda cleanup starvation** — `RunAsync`'in tek `try/catch`'i yüzünden `ProcessOutboxMessagesAsync` patlarsa `CleanupProcessedMessagesAsync` hiç çağrılmıyor (`InboxProcessor` de aynı şekilde). Production'da döngülerin ~%33'ü DB connect timeout ile düştüğü için retention cleanup tam da tablo büyürken aç kalıyor. | T7 review'ında tartışıldı, **bilinçli ertelendi**: `finally` ile ayırmak her hata döngüsünde **garantili** ek bağlantı denemesi ekler — havuz baskısını tam da en kötü anda artırır. Sürekli kesintide cleanup zaten başarısız olacağı için ayırmak bir şey kazandırmaz. Tek bir cleanup penceresini kaçırmak, bir sonraki başarılı döngüde kendini onarıyor (cleanup zaten aralık kapılı). Sürekli baskı altında tablo büyümesi izlenmeli. |
| `lease_version` fencing, ayrı dead-letter tablosu, Drasi | Spec §7 — bilinçli kapsam dışı |
