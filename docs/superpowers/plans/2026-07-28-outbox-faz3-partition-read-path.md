# Outbox Faz 3 — Partition Okuma Yolu

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 10 outbox replica'sının aynı index önekini taramasını bitirmek — sinyalin taşıdığı partition bilgisini lease sorgusuna kadar götürüp her pod'un ayrık bir index aralığı taramasını sağlamak, ve tüm bunu runtime'da kapatılabilir tutmak.

**Architecture:** `IOutboxSignalCoordinator.WaitAsync` zaten hangi partition'ın sinyallendiğini döndürüyor ama `OutboxBackgroundService` bunu atıyor. Faz 3 o anahtarları processor'a, oradan `IOutboxLeaseStore`'a taşır; lease sorgusu `PartitionId = ANY(...)` ile daralır. **Fallback poll her zaman filtresiz kalır** — sinyali kaybolan bir partition aç kalmasın. Tüm davranış `PartitionedLeasingEnabled` (varsayılan `false`) arkasında.

**Tech Stack:** .NET 10, EF Core 10.0.4, Npgsql 10.0.2, xUnit + Shouldly, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-07-28-outbox-signal-partition-design.md` §3

**Branches (ikisi de Faz 2 ucundan dallanacak):**
- Aether: `feature/outbox-faz2-signal` @ `/Users/U0B006/Documents/repos/burgan-tech/aether`
- vNext: `feature/outbox-faz2-signal` @ `/Users/U0B006/Documents/repos/burgan-tech/vnext`

> ⚠️ vNext'teki `a7797dae` (local-link) **revert edilmeyecek**. Kullanıcı tüm fazlar bitince
> test edip PR öncesi temizleyecek. vNext bu branch'te CI'da derlenmez; bu bilinçli.

---

## Faz 3 neden gerekli, ve neden şimdi ölçülü olmalı

Ölçülen 3 domain düşük hacimli (0,073 msg/sn) ve orada çekişme yok. Partition'ın karşılığını
vereceği rejim **IDM/login prod hacmi** — kullanıcının açıkça işaret ettiği, bu veride
bulunmayan durum. Faz 1 kolonu ve index'i o yüzden şimdiden hazırladı.

Çekişme mekanizması: 10 pod aynı anda `FOR UPDATE SKIP LOCKED` ile aynı partial index önekini
tarıyor. N'inci pod, kendinden öncekilerin kilitlediği ~(N-1)×batch satırın üzerinden atlamak
zorunda. Partition filtresi bu taramaları ayrık aralıklara böler.

**Ama bu iş kapatılabilir olmalı.** Faz 1 review'ında ölçüldüğü gibi, `PartitionId` sabit-0
iken bile index planı kötüleşmiyor; yani partition kapalıyken hiçbir maliyet yok. Açıkken
sorun çıkarsa deploy beklemeden geri alınabilmeli.

---

## Spec'ten sapma (bilinçli, gerekçeli)

Spec §3.5 "aynı değişiklik Inbox dispatcher'ına uygulanır" diyor. **Inbox'a uygulanmayacak.**

Gerekçe: partition filtresinin kaynağı sinyaldir, inbox'ta sinyal yok (Faz 2 bilinçli olarak
inbox'ı kapsam dışı bıraktı — inbox'ı uyandıran zaten Dapr teslimi). Kaynağı olmayan bir
filtre her zaman `null` olur, yani değişiklik tamamen etkisiz kod olurdu. Inbox'a sinyal
eklenirse Faz 3'ün inbox karşılığı da o zaman anlamlı olur.

---

## Ordering: değişmiyor, ve değişmediğini söylemek önemli

Spec §3.4: partition eklemek **ordering garantisi vermez ve mevcut davranışı değiştirmez.**
Bugün de cross-message sıralama garantisi yok — batch içi `foreach` sıralı ama 10 replica
arasında sıra garanti değil.

Partition filtresi bunu ne iyileştirir ne kötüleştirir: aynı `Subject`'e sahip event'ler
zaten aynı partition'a düşüyor (Faz 1'deki hash `Subject ?? Id` üzerinden), ama aynı
partition'ı farklı zamanlarda farklı pod'lar leaseleyebilir ve publish paraleldir.

Bu yüzden **hiçbir görevde "artık sıralı" ima eden bir yorum veya doküman yazma.** Doküman §14
"partition başına sequential publish" bilinçli olarak yapılmıyor — sıralama garantisi vermek
yeni bir sözleşmedir ve bu işin kapsamı değil.

---

## File Structure

### Aether — değiştirilecek

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `.../BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs` | `PartitionedLeasingEnabled` | T1 |
| `.../BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs` | Opsiyonel partition filtresi | T1 |
| `.../BBT.Aether.Core/BBT/Aether/Events/NullOutboxLeaseStore.cs` | Yeni imzaya uyum | T1 |
| `.../BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs` | Partition'lı lease SQL | T2 |
| `.../BBT.Aether.Core/BBT/Aether/Events/IOutboxProcessor.cs` | Partition parametresi | T3 |
| `.../BBT.Aether.Infrastructure/.../Processing/OutboxProcessor.cs` | Filtreyi lease'e geçir | T3 |
| `.../BBT.Aether.Infrastructure/.../Processing/OutboxBackgroundService.cs` | Sinyal anahtarlarını çöz | T4 |

### Aether — yeni

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `.../BBT.Aether.Core/BBT/Aether/Events/Processing/PartitionFilter.cs` | Sinyal anahtarları → partition listesi (saf fonksiyon) | T4 |

### Aether — testler

| Dosya | Görev |
|---|---|
| `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs` (ekleme) | T2 |
| `framework/test/BBT.Aether.Infrastructure.Tests/.../Processing/PartitionFilterTests.cs` (yeni) | T4 |

### vNext

| Dosya | Sorumluluk | Görev |
|---|---|---|
| `workers/BBT.Workflow.Workers.Outbox/appsettings.json` | `PartitionedLeasingEnabled` | T5 |

---

## Task 1: Options ve lease store sözleşmesi

**Files:**
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs`
- Modify: `framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs`
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxLeaseStore.cs`

- [ ] **Step 1: Flag'i ekle**

`AetherOutboxOptions.cs`, `PartitionCount`'un altına:

```csharp
    /// <summary>
    /// Whether the dispatcher leases partition-disjoint batches when a wake-up signal names a
    /// partition. When false the lease query is never filtered, which is the pre-partition
    /// behaviour.
    /// </summary>
    /// <remarks>
    /// Safe to flip at runtime in either direction. Rows keep whatever <c>PartitionId</c> they
    /// were written with, and fallback polling is unfiltered regardless, so a row can never be
    /// stranded by turning this on or off — only its dispatch can be delayed to the next
    /// fallback interval.
    /// </remarks>
    public bool PartitionedLeasingEnabled { get; set; }
```

- [ ] **Step 2: Lease sözleşmesine opsiyonel filtre ekle**

`IOutboxLeaseStore.cs`'te `LeaseBatchAsync` imzasını değiştir:

```csharp
    /// <param name="partitionIds">
    /// When supplied, only rows in these partitions are considered. When null the query is
    /// unfiltered — which is what fallback polling always does, so that a partition whose
    /// signal was lost is never stranded.
    /// </param>
    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyCollection<short>? partitionIds = null,
        CancellationToken cancellationToken = default);
```

Parametreyi `cancellationToken`'dan **önce** ve opsiyonel koy: mevcut çağıranlar
(`OutboxProcessor`, testler) kaynak uyumlu kalır ve derlenmeye devam eder.

`using System.Collections.Generic;` gerekiyorsa ekle.

- [ ] **Step 3: `NullOutboxLeaseStore`'u uyumla**

Yeni parametreyi imzasına ekle; gövdesi boş liste döndürmeye devam etsin.

- [ ] **Step 4: Derle**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether
dotnet build framework/BBT.Aether.slnx
```

Beklenen: 0 error. Opsiyonel parametre sayesinde `NpgsqlOutboxLeaseStore` ve
`NpgsqlInboxLeaseStore` henüz değişmeden derlenmeli — **`NpgsqlInboxLeaseStore` `IInboxLeaseStore`
implement ediyor, ona dokunma.** Hangi lease store'ların bu arayüzü implement ettiğini
doğrula ve raporla; beklenmedik bir implementasyon varsa dur.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/AetherOutboxOptions.cs \
        framework/src/BBT.Aether.Abstractions/BBT/Aether/Events/IOutboxLeaseStore.cs \
        framework/src/BBT.Aether.Core/BBT/Aether/Events/NullOutboxLeaseStore.cs
git commit -m "feat(outbox): add an optional partition filter to the lease contract

Optional and defaulted so every existing caller keeps compiling; nothing passes
a filter yet. PartitionedLeasingEnabled ships disabled."
```

---

## Task 2: Partition'lı lease sorgusu

**Neden ayrı SQL:** filtreyi `(@partitions IS NULL OR "PartitionId" = ANY(@partitions))`
şeklinde tek sorguya gömmek cazip ama planlayıcıyı zorlar — Faz 1 review'ı, partial index'in
seçilmesinin planner'ın değerleri sabit görmesine bağlı olduğunu ölçmüştü. `WHERE` parçasını
koşullu kurmak iki net plan üretir.

**Files:**
- Modify: `framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs`
- Test: `framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs`

- [ ] **Step 1: Başarısız testleri yaz**

`NpgsqlLeaseStoreTests.cs`'e ekle. Dosyanın mevcut `InsertPendingMessageAsync` yardımcısı
`Subject` almıyorsa, partition'ı doğrudan SQL ile ayarlayan bir yardımcı ekle:

```csharp
    private async Task SetPartitionAsync(short partitionId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE \"{_schema}\".\"OutboxMessages\" SET \"PartitionId\" = {partitionId}";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task LeaseBatch_with_a_partition_filter_only_returns_matching_rows()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);
        await SetPartitionAsync(7);

        await using var scope = sp.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IOutboxLeaseStore>();

        using (currentSchema.Change(_schema))
        {
            IReadOnlyList<BBT.Aether.Events.OutboxMessage> wrongPartition;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                wrongPartition = await leaseStore.LeaseBatchAsync(
                    10, "worker-1", TimeSpan.FromSeconds(30), new short[] { 3 });
                await uow.CommitAsync();
            }

            wrongPartition.ShouldBeEmpty();

            IReadOnlyList<BBT.Aether.Events.OutboxMessage> rightPartition;
            await using (var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
            {
                rightPartition = await leaseStore.LeaseBatchAsync(
                    10, "worker-1", TimeSpan.FromSeconds(30), new short[] { 7 });
                await uow.CommitAsync();
            }

            rightPartition.Count.ShouldBe(1);
            rightPartition[0].PartitionId.ShouldBe((short)7);
        }
    }

    [Fact]
    public async Task LeaseBatch_with_a_null_filter_is_unfiltered()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);
        await SetPartitionAsync(42);

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
                // Fallback polling passes null and must see every partition.
                leased = await leaseStore.LeaseBatchAsync(10, "worker-1", TimeSpan.FromSeconds(30), null);
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task LeaseBatch_with_several_partitions_returns_rows_from_each()
    {
        var sp = BuildProvider();
        await SetupSchemaAsync(sp);
        await InsertPendingMessageAsync(sp);
        await SetPartitionAsync(5);
        await InsertPendingMessageAsync(sp);

        // The second insert lands on whatever partition its subject hashes to; force it to 9.
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{_schema}"."OutboxMessages" SET "PartitionId" = 9
                WHERE "Id" = (SELECT "Id" FROM "{_schema}"."OutboxMessages"
                              WHERE "PartitionId" <> 5 LIMIT 1)
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
                leased = await leaseStore.LeaseBatchAsync(
                    10, "worker-1", TimeSpan.FromSeconds(30), new short[] { 5, 9 });
                await uow.CommitAsync();
            }

            leased.Count.ShouldBe(2);
        }
    }
```

İkinci insert'in gerçekten farklı bir partition'a düştüğünü varsayma — yukarıdaki SQL onu
zorluyor, ama çalıştırıp iki satırın 5 ve 9'da olduğunu doğrula. Değilse testi düzelt,
zayıflatma.

- [ ] **Step 2: Çalıştır, ilk testin başarısız olduğunu gör**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~LeaseBatch_with_a_partition_filter"
```

Beklenen: FAIL — filtre henüz uygulanmadığı için `wrongPartition` boş yerine 1 satır döner.

- [ ] **Step 3: SQL'i koşullu kur**

`NpgsqlOutboxLeaseStore.LeaseBatchAsync`'te, `command.CommandText` atamasından önce filtre
parçasını kur:

```csharp
        var partitionFilter = partitionIds is { Count: > 0 }
            ? "\n                  AND \"PartitionId\" = ANY(@partitionIds)"
            : string.Empty;
```

`WHERE` bloğuna yerleştir — mevcut `AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= @now)`
satırından hemen sonra `{partitionFilter}` gelecek şekilde. Sorgunun geri kalanı
(`ORDER BY "CreatedAt"`, `LIMIT @batchSize`, `FOR UPDATE SKIP LOCKED`, `RETURNING` listesi)
aynen kalsın.

Parametreyi yalnızca filtre varken ekle:

```csharp
        if (partitionIds is { Count: > 0 })
        {
            var p = command.CreateParameter();
            p.ParameterName = "@partitionIds";
            p.Value = partitionIds.ToArray();
            command.Parameters.Add(p);
        }
```

**Dizi tipi eşlemesi.** Npgsql `short[]`'i `smallint[]`'e kendiliğinden eşler ve kolon da
`smallint`, yani `= ANY(@partitionIds)` doğrudan çalışmalı. Ama bu dosya jenerik
`DbParameter` (`command.CreateParameter()`) kullanıyor, `NpgsqlParameter` değil — jenerik yol
tip çıkarımını her zaman aynı şekilde yapmayabilir.

T2 Step 1'deki `LeaseBatch_with_a_partition_filter_only_returns_matching_rows` testi bunu
zaten yakalar: yanlış eşleme olursa sorgu hiç satır döndürmez ve testin ikinci yarısı
(`rightPartition.Count.ShouldBe(1)`) kırmızıya döner. Test kırmızı kalırsa parametreyi
`NpgsqlParameter` ile `NpgsqlDbType.Array | NpgsqlDbType.Smallint` vererek açıkça tiple ve
bunu raporla.

Bu ayrımın önemi: sessizce yanlış tiplenen bir dizi "partition'da iş yok" gibi görünür —
hata vermez, yalnızca satırlar fallback aralığına kadar bekler.

Boş liste (`Count == 0`) filtresiz sayılır — `= ANY('{}')` hiçbir satır döndürmez ve bu
istenen davranış değil. Bunu bir yorumla belirt.

- [ ] **Step 4: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Postgres.Tests --filter "FullyQualifiedName~NpgsqlLeaseStoreTests"
```

Beklenen: mevcut testler + 3 yeni test, hepsi PASS.

- [ ] **Step 5: Commit**

```bash
git add framework/src/BBT.Aether.Npgsql/BBT/Aether/Events/NpgsqlOutboxLeaseStore.cs \
        framework/test/BBT.Aether.Postgres.Tests/NpgsqlLeaseStoreTests.cs
git commit -m "feat(outbox): filter the lease query by partition when one is supplied

The WHERE fragment is built conditionally rather than using a NULL-or-ANY
predicate, so the planner sees two clean shapes and can still prove the partial
dispatch index covers the query."
```

---

## Task 3: Processor filtreyi geçirsin

**Files:**
- Modify: `framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxProcessor.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs`

- [ ] **Step 1: Sözleşmeyi genişlet**

`IOutboxProcessor.cs`:

```csharp
    /// <param name="partitionIds">
    /// Partitions a wake-up signal named, or null to lease unfiltered. Fallback polling always
    /// passes null so a partition whose signal was lost is never stranded.
    /// </param>
    Task<int> RunAsync(
        IReadOnlyCollection<short>? partitionIds = null,
        CancellationToken cancellationToken = default);
```

Opsiyonel ve `cancellationToken`'dan önce — mevcut çağıranlar (testler dahil) derlenmeye
devam etsin.

- [ ] **Step 2: Processor'da geçir**

`OutboxProcessor.RunAsync` imzasını uyumla ve `ProcessOutboxMessagesAsync`'e taşı; oradan
lease çağrısına:

```csharp
                messages = (await leaseStore.LeaseBatchAsync(
                    options.BatchSize, workerId, options.LeaseDuration,
                    options.PartitionedLeasingEnabled ? partitionIds : null,
                    cancellationToken)).ToList();
```

Flag kapalıyken **her zaman `null`** geçilir — çağıran ne gönderirse göndersin. Kill-switch'in
tek noktada olması önemli; ayrıca bunu bir yorumla belirt.

`CleanupProcessedMessagesAsync` partition'dan etkilenmez — retention silmesi tüm partition'ları
kapsamalı. Ona dokunma.

- [ ] **Step 3: Derle ve tüm suite'i çalıştır**

```bash
dotnet build framework/BBT.Aether.slnx
dotnet test framework/BBT.Aether.slnx
```

Mevcut çağıranların kırılıp kırılmadığını raporla. `ProcessorFailurePropagationTests` ve
`OutboxCleanupTests` `RunAsync()` çağırıyor — opsiyonel parametre sayesinde derlenmeli.

- [ ] **Step 4: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/IOutboxProcessor.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxProcessor.cs
git commit -m "feat(outbox): pass the signalled partitions through to the lease query

PartitionedLeasingEnabled is enforced here, in one place, so a caller cannot
bypass the kill switch by supplying a filter."
```

---

## Task 4: Dispatcher sinyal anahtarlarını çözsün

**Neden ayrı bir saf fonksiyon:** anahtar→partition dönüşümünün üç kuralı var ve üçü de
yanlış yapılırsa sessiz. Saf fonksiyon olarak test edilebilir olması, `BackgroundService`
içinde gömülü olmasından iyi.

Kurallar:
- boş anahtar kümesi (fallback timeout) → `null`, yani filtresiz
- içinde `AllPartitions` (-1) olan bir anahtar varsa → `null`, filtresiz
- aksi hâlde → benzersiz partition id listesi

**Files:**
- Create: `framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/PartitionFilter.cs`
- Modify: `framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs`
- Test: `framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/PartitionFilterTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

```csharp
using System;
using System.Linq;
using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Shouldly;
using Xunit;

namespace BBT.Aether.Events.Processing;

public sealed class PartitionFilterTests
{
    [Fact]
    public void No_signals_means_unfiltered()
    {
        PartitionFilter.Resolve(Array.Empty<OutboxSignalKey>()).ShouldBeNull();
    }

    [Fact]
    public void A_check_all_signal_means_unfiltered()
    {
        var keys = new[]
        {
            new OutboxSignalKey("sys_queues", 3),
            new OutboxSignalKey("sys_queues", OutboxWakeupSignal.AllPartitions),
        };

        PartitionFilter.Resolve(keys).ShouldBeNull();
    }

    [Fact]
    public void Distinct_partitions_are_collected()
    {
        var keys = new[]
        {
            new OutboxSignalKey("sys_queues", 3),
            new OutboxSignalKey("sys_queues", 7),
            new OutboxSignalKey("sys_queues", 3),
        };

        var result = PartitionFilter.Resolve(keys);

        result.ShouldNotBeNull();
        result!.OrderBy(p => p).ShouldBe(new short[] { 3, 7 });
    }

    [Fact]
    public void Keys_from_different_schemas_are_all_included()
    {
        // The dispatcher already scopes itself by schema, and the endpoint rejects foreign
        // schemas before they reach the coordinator, so this is defence in depth rather than
        // a case expected in practice.
        var keys = new[]
        {
            new OutboxSignalKey("sys_queues", 1),
            new OutboxSignalKey("other", 2),
        };

        var result = PartitionFilter.Resolve(keys);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(2);
    }
}
```

- [ ] **Step 2: Çalıştır, derlenmediğini gör**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~PartitionFilterTests"
```

Beklenen: `CS0246 ... 'PartitionFilter' could not be found`.

- [ ] **Step 3: Saf fonksiyonu oluştur**

```csharp
using System.Collections.Generic;
using System.Linq;

namespace BBT.Aether.Events.Processing;

/// <summary>
/// Turns the signal keys a dispatcher woke on into a lease-query partition filter.
/// </summary>
public static class PartitionFilter
{
    /// <summary>
    /// Returns the distinct partitions to lease from, or null meaning "lease unfiltered".
    /// </summary>
    /// <remarks>
    /// Unfiltered is the safe answer and is returned whenever the signals do not narrow things
    /// down: no signals at all means the fallback timeout fired, and a check-all signal means a
    /// producer touched more partitions in one transaction than it was worth naming
    /// individually. Fallback polling being unfiltered is what stops a partition whose signal
    /// was lost from being stranded.
    /// </remarks>
    public static IReadOnlyCollection<short>? Resolve(IReadOnlyCollection<OutboxSignalKey> keys)
    {
        if (keys.Count == 0) return null;
        if (keys.Any(k => k.PartitionId == OutboxWakeupSignal.AllPartitions)) return null;

        return keys.Select(k => k.PartitionId).Distinct().ToArray();
    }
}
```

- [ ] **Step 4: Testleri çalıştır**

```bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests --filter "FullyQualifiedName~PartitionFilterTests"
```

Beklenen: 4 test PASS.

- [ ] **Step 5: Dispatcher'a bağla**

`OutboxBackgroundService.ExecuteAsync`'te, döngü **dışında** bir alan tut:

```csharp
        IReadOnlyCollection<short>? partitions = null;
```

`processor.RunAsync` çağrısını değiştir:

```csharp
                var processed = await processor.RunAsync(partitions, stoppingToken);
```

Ve döngü sonundaki sinyal beklemesinin sonucunu artık kullan:

```csharp
                // Which partitions were signalled narrows the next cycle's lease. An empty
                // result means the fallback timeout fired, and PartitionFilter turns that into
                // an unfiltered sweep — that sweep is what recovers a partition whose signal
                // was lost.
                var keys = await signalCoordinator.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                partitions = PartitionFilter.Resolve(keys);
```

Hata backoff'u dalında (`Task.Delay`) `partitions`'ı **`null`'a çek** — bir hatadan sonraki
ilk deneme her şeyi taramalı, çünkü backoff sırasında biriken sinyaller okunmadı:

```csharp
            if (backingOffAfterError)
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                partitions = null;
            }
```

Bunu bir yorumla gerekçelendir.

- [ ] **Step 6: Derle ve tüm suite'i çalıştır**

```bash
dotnet build framework/BBT.Aether.slnx
dotnet test framework/BBT.Aether.slnx
```

- [ ] **Step 7: Commit**

```bash
git add framework/src/BBT.Aether.Core/BBT/Aether/Events/Processing/PartitionFilter.cs \
        framework/src/BBT.Aether.Infrastructure/BBT/Aether/Events/Processing/OutboxBackgroundService.cs \
        framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/Events/Processing/PartitionFilterTests.cs
git commit -m "feat(outbox): narrow the next lease to the partitions that were signalled

The coordinator's keys were being discarded. Unfiltered remains the answer
whenever the signals do not narrow things down, and after an error back-off,
since signals accumulated during it were never read."
```

---

## Task 5: vNext konfigürasyonu

**Files:**
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json`

- [ ] **Step 1: Flag'i açıkça kapalı olarak pinle**

`Aether:Outbox` bloğuna, `PartitionCount`'un yanına:

```json
      "PartitionedLeasingEnabled": false,
```

**Kapalı ship ediliyor.** Ölçülen domain'lerde çekişme yok; açmanın karşılığı IDM hacminde.
Açıkça yazılması, varsayılana güvenmekten iyi — birinin "bu ayar nerede" diye aramasını
önler ve açma kararının bilinçli olduğunu gösterir.

- [ ] **Step 2: JSON'u ve binding'i doğrula**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext
python3 -m json.tool workers/BBT.Workflow.Workers.Outbox/appsettings.json > /dev/null && echo "JSON ok"
dotnet build BBT.Workflow.slnx
```

Binding'i kanıtla: `false` zaten varsayılan olduğu için bağlandığını göstermez. Kopyada
`true` yapıp yeniden bağla, yansıdığını gör, gerçek dosyanın hâlâ `false` okuduğunu doğrula.
Throwaway'i sil.

- [ ] **Step 3: Commit**

`mapp.cs`'i dışarıda tutmak için yolları açıkça stage'le.

```bash
git add workers/BBT.Workflow.Workers.Outbox/appsettings.json
git commit -m "chore(outbox-worker): pin partitioned leasing off

Contention only appears at IDM-scale volume; the measured domains do not have
it. Written explicitly so enabling it is a visible decision."
```

`git show --stat HEAD` ile `mapp.cs`'in commit'te olmadığını doğrula.

---

## Task 6: Doğrulama

Bu bir **doğrulama** görevi. Production kodu değiştirme; kusur bulursan raporla.
`mapp.cs` staged duruyor — **yazan hiçbir git komutu çalıştırma.**

- [ ] **Step 1: Kapalıyken davranış değişmemeli**

`PartitionedLeasingEnabled = false` ile worker'ı çalıştır ve Faz 2'nin sonucunu tekrarla:
gerçek bir orchestration yazımı → sinyal → worker uyanması. Beklenen: **saniye altı**,
Faz 2'de ölçülen ~147 ms mertebesinde.

Bu, kill-switch'in gerçekten kapatıyor olduğunun kanıtı. Değişiklik varsa flag kapalıyken
bir şeye dokunmuşuz demektir.

- [ ] **Step 2: Açıkken partition filtresi gerçekten uygulanıyor mu**

`PartitionedLeasingEnabled` değerini `true` yap, worker'ı yeniden başlat.

İki farklı partition'a satır yaz (farklı `Subject` değerleri farklı partition'lara hash'lenir
— hangi subject'in hangi partition'a düştüğünü `MessagePartitionResolver.Resolve` ile
hesaplayabilirsin, `PartitionCount = 64`). Yalnızca birinin partition'ı için sinyal yayınla.

Beklenen: sinyallenen partition'daki satır hemen leaselenir; diğeri **fallback aralığına
kadar bekler** (300 sn — sabırlı ol ya da geçici olarak `MaxPollingInterval`'ı düşür ve
düşürdüğünü raporla).

Bu, filtrenin fiilen daralttığının kanıtı. Her iki satır da hemen leaselenirse filtre
uygulanmıyor demektir.

- [ ] **Step 3: Fallback'in filtresiz olduğunu doğrula**

Sinyal yayınlamadan bir satır yaz. Fallback aralığı dolduğunda leaselenmeli. Bu, sinyali
kaybolan bir partition'ın aç kalmadığının kanıtı — Faz 3'ün en önemli güvenlik özelliği.

- [ ] **Step 4: Sorgu planını doğrula**

Faz 1 runbook'undaki `EXPLAIN` kontrolünü partition'lı sorgu için tekrarla:

```sql
EXPLAIN (ANALYZE, BUFFERS)
UPDATE sys_queues."OutboxMessages" SET "Status" = 1
WHERE "Id" IN (
    SELECT "Id" FROM sys_queues."OutboxMessages"
    WHERE "Status" IN (0, 1)
      AND ("LockedUntil" IS NULL OR "LockedUntil" < now())
      AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= now())
      AND "PartitionId" = ANY(ARRAY[7]::smallint[])
    ORDER BY "CreatedAt" LIMIT 100 FOR UPDATE SKIP LOCKED);
```

Beklenen: `IX_OutboxMessages_Dispatch` kullanılır ve `PartitionId` artık **öndeki eşitlik
koşulu** olduğu için prefix scan olur — Faz 1'de ölçülen "sabit kolon, nötr" durumundan
kazanca dönmüş olmalı. Planı ve maliyeti raporla; Faz 1 review'ındaki filtresiz plan
(maliyet ~3912) ile karşılaştır.

- [ ] **Step 5: Regresyon**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/aether && dotnet test framework/BBT.Aether.slnx
cd /Users/U0B006/Documents/repos/burgan-tech/vnext && dotnet build BBT.Workflow.slnx
```

vNext test suite'ini **çalıştırma** — 191 bilinen pre-existing hata, ilgisiz ve yavaş.

- [ ] **Step 6: Raporla**

Değiştirdiğin geçici konfigürasyonları geri al ve `git status`'ün senin bıraktığın bir şey
göstermediğini doğrula (`mapp.cs` hariç, o zaten staged).

---

## Bu planın kapsamı dışında

| Konu | Nerede |
|---|---|
| Inbox'a partition okuma yolu | Sinyal kaynağı yok; inbox'a sinyal eklenirse anlamlı olur |
| Replica 10 → 2-3 | Bu repolarda replica tanımı yok (Helm/K8s yok); deployment reposunda |
| `PartitionedLeasingEnabled`'ı prod'da açmak | IDM hacmi geldiğinde, tetikleyici metrikle (lease p95 replica sayısıyla artıyor mu) |
| Npgsql `Max Pool Size`, PgBouncer tuning | Spec §10 — bu plan pool sorununu çözmez |
| `a7797dae` local-link revert'i | Kullanıcı tüm fazlar bitince test edip PR öncesi temizleyecek |
