# Outbox: Wake-up Signal ve DB Yükü Azaltma — Tasarım

**Tarih:** 2026-07-28
**Durum:** Taslak — kullanıcı onayı bekliyor (log kanıtıyla revize edildi)
**Kapsam:** Aether SDK (`BBT.Aether.Infrastructure`, `BBT.Aether.Npgsql`, `BBT.Aether.Core`) + vNext worker konfigürasyonu
**Kaynak doküman:** `outbox_signal.md` (Partition-Aware Outbox Dispatcher ve Signal-Based Processing)
**Önceki iş:** `docs/superpowers/specs/2026-06-24-inbox-outbox-redesign.md`

---

## 0. Yönetici Özeti

27 Tem 2026 preprod Elastic export'u (18:16–18:28, 736 sn, 11.285 tekil satır, 3 domain)
tasarımın bir varsayımını çürüttü:

**Outbox, DB baskısının nedeni değil — kurbanı.** DB baskı imzalarının %94'ü API pod'larında
(contract-app 139, onboarding-app 100, discovery-app 78); outbox worker'larda toplam **3**.
Bu işin tamamı yapılsa bile pool sorunu çözülmez. O ayrı bir iş kalemidir (§10).

Buna karşılık kanıt, **döngünün kendi verimsizliğini** ortaya koydu: outbox yayınladığı **her
bir mesaj için ~22 DB transaction** harcıyor ve döngülerinin **%33'ü DB'ye bağlanamadığı için
hata veriyor**. Ayrıca aktif veri kaybı üreten bir bug bulundu (§4-B1).

### İki özellik, iki farklı hacim rejimi

Ölçülen 3 domain düşük hacimli (0,073 msg/sn). **IDM/login gibi süreçler prod'da bunun
mertebelerce üzerinde olacak.** Sinyal ve partition farklı rejimlerde işe yarıyor, bu yüzden
**ikisi de yapılır**:

| Rejim | Darboğaz | Çözen özellik |
|---|---|---|
| Düşük/orta hacim (bugünkü contract, onboarding, discovery; IDM off-peak) | Boş poll — 30 pod boş tabloyu yokluyor, mesaj başına ~22 tx | **Sinyal** (Faz 2). Worker uyur, iş gelince uyanır |
| Yüksek hacim (IDM peak) | `SKIP LOCKED` çekişmesi — 10 pod aynı index önekini tarıyor, N'inci pod ~(N-1)×batch kilitli satırı atlıyor | **Partition** (Faz 3). Pod'lar ayrık index aralıkları tarar |

Yüksek hacimde worker zaten hiç uyumadığı için sinyalin katkısı azalır; düşük hacimde
çekişme olmadığı için partition'ın katkısı azalır. İkisi birbirinin yerine geçmez.

### Tek yönlü kapılar — şimdi karar verilmeli

`partition_id` kolonu ve hash sözleşmesi sonradan değiştirilmesi pahalı kararlar. IDM
tablosu büyüdükten sonra backfill yapmak, şimdi boş tabloya kolon eklemekten kat kat
maliyetli. Bu yüzden:

- **Yazma yolu (kolon + hash + doldurma) Faz 1'de** — şimdi, zorunlu, düşük hacimli
  domain'lerde bile.
- **Okuma yolu (partition'lı lease + sinyal hedefleme) Faz 3'te** — always-on, flag yok.

---

## 1. Problem

vNext preprod'da PostgreSQL bağlantı doygunluğu var. Aynı zamanda polling aralığı
büyütüldüğünde event yayınlama gecikmesi kabul edilemez seviyeye çıkıyor — yani bugün **DB
maliyeti ile latency arasında doğrudan bir takas** var.

**Hedef:** Bu takası kırmak; outbox döngüsünün DB ayak izini düşürürken yayınlama gecikmesini
de düşürmek.

### Topoloji

| Bileşen | Replica (domain başına) | 3 domain toplamı |
|---|---|---|
| Orchestration API | 10 | 30 |
| Execution API | 20 | 60 |
| Outbox Worker | 10 | 30 |
| Inbox Worker | 10 | 30 |

Domain'ler: `contract`, `onboarding`, `discovery` (loglardan doğrulandı). **~150 pod.**

- PostgreSQL **tek**, PgBouncer **tek**, **transaction mode**, tüm domain'ler paylaşıyor.
- Dapr, Vault, OpenTelemetry paylaşımlı.
- Redis Sentinel (state store + pubsub) **domain başına ayrı**, 5 replica.

`DispatchStrategy = AlwaysUseOutbox` — her domain event outbox tablosundan geçiyor.

---

## 2. Mevcut Durum (koddan doğrulandı)

Outbox tamamen **Aether SDK'da**. vNext yalnızca `Aether:Outbox` section'ı ile konfigüre
ediyor.

### Zaten karşılanan doküman maddeleri

| Doküman bölümü | Durum |
|---|---|
| §13.1 Broker publish, lease transaction'ının dışında | ✅ 3 fazlı: lease UoW → publish (tx yok) → outcome UoW |
| §13.2 `FOR UPDATE SKIP LOCKED` batch lease | ✅ `NpgsqlOutboxLeaseStore` |
| §13.3 `locked_by` / worker identity | ✅ `WorkerIdentity` |
| §16 Exponential backoff retry | ✅ `CalculateNextRetryTime` |
| §17 Dead-letter state | ✅ `OutboxMessageStatus.DeadLetter` |
| §15.1 Completion fencing | ✅ Kısmen — `ExecuteUpdate ... WHERE LockedBy=@me AND LockedUntil>now` |

### Doğrulanan ön koşullar

- **`IUnitOfWork.OnCompleted(Func<IUnitOfWork, Task>)` Aether'da zaten var** → doküman §8.1
  için framework işi yok.
- **`LISTEN`/`NOTIFY` kullanılamaz** — PgBouncer transaction mode session-scoped `LISTEN`'i
  bozar. Dapr pub/sub doğru taşıyıcı.
- **Outbox worker zaten `WebApplication`** (Dapr client + pubsub) → `[Topic]` endpoint küçük
  ekleme.
- **Dapr Redis pubsub competing-consumer** — repo'da hiçbir yerde `consumerID` set edilmemiş,
  Dapr varsayılan olarak app-id kullanır → aynı app-id'nin tüm replica'ları tek consumer
  group'ta → bir sinyal **tam olarak bir** pod'a gider. ⚠️ `vnext-pubsub-broadcast` diye ayrı
  bir component var; preprod'da `consumerID`'nin pod-başına unique set edilmediği **deploy
  repo'sundan doğrulanmalı** (§9-3). Unique olsaydı her sinyal 10 pod'a giderdi.

---

## 3. Log Kanıtı

**Kaynak:** `~/Downloads/Untitled Discover session(3,4,5).csv`, 27 Tem 2026 18:16:20–18:28:36
(736 sn), 11.285 tekil satır. *(Kullanıcının ilk attığı (1) ve (2) numaralı export'lar
Elastic'te timeout almış, veri içermiyor.)*

> ⚠️ **Örneklem sınırı:** Bu üç domain (contract, onboarding, discovery) **düşük hacimli**.
> IDM/login gibi yüksek trafikli süreçler bu veride **temsil edilmiyor**. Aşağıdaki K1
> rakamları "outbox her zaman böyle düşük hacimlidir" anlamına gelmez; yalnızca bugünkü
> ölçülen domain'ler için geçerlidir. Partition kararı bu örnekleme dayandırılamaz (§0).

### K1 — Ölçülen domain'lerde outbox hacmi düşük

```
Yayınlanan mesaj (736 sn / 30 pod) : 54        → 0,073 msg/sn
Lease olayı                        : 22
Lease batch boyutları              : 1(×14), 2, 3(×3), 5(×2), 6, 13
Konfigüre batch size               : 100       → hiç yaklaşılmıyor
```

→ **Bu domain'lerde** `SKIP LOCKED` çekişmesi oluşmuyor; darboğaz boş poll (K2). Partition'ın
karşılığını vereceği rejim (IDM peak) bu veride yok — bu yüzden partition Faz 3'e alındı,
iptal edilmedi, ve yazma yolu Faz 1'de ship ediliyor (§0 "tek yönlü kapılar").

### K2 — Döngü, mesaj başına ~22 DB transaction harcıyor

`OutboxBackgroundService` backoff'u `processed > 0` olduğunda **`BusyPollingInterval`'a
(100 ms) sıfırlanıyor** — batch 100'de 1 gelmiş olsa bile. 100 ms'den 60 sn tavanına geri
tırmanmak 10 poll ve ~102 saniye sürüyor.

```
Taban (30 pod, 60 sn tavanda)        : 30 × 736/60      ≈  368 poll
Tırmanma (22 lease olayı × 10 poll)  :                  ≈  220 poll
Toplam                               :                  ≈  588 poll
× 2 tx/poll (lease + cleanup, §B2)   :                  ≈ 1.176 tx / 736 sn
                                                        ≈ 1,6 tx/sn
Yayınlanan mesaj başına              :                  ≈ 22 DB transaction
```

Inbox worker aynı döngü şeklini kullanıyor (30 pod) → benzer mertebe.

### K3 — DB baskısı API'lerde, worker'larda değil

| Servis | DB baskı imzası |
|---|---|
| vnext-contract-app | 139 |
| vnext-onboarding-app | 100 |
| vnext-discovery-app | 78 |
| *worker-outbox (3 domain toplamı)* | **3** |
| *worker-inbox (3 domain toplamı)* | **11** |

→ **Outbox işi pool sorununu çözmez.**

### K4 — Hata imzası "client pool exhaustion" DEĞİL

```
"connection pool has been exhausted"                    :   0
NpgsqlConnector.ConnectAsync / RawOpen timeout          : 287
"The operation has timed out"                           : 212
"A task was canceled" / canceled                        : 109
EF "likely due to a transient failure"                  :  76
"Failed to connect to <ip>:<port>"                      :  49
Health check database Unhealthy                         :  57
"connection is already in a transaction"                :   0
```

**Bu, 2026-06-19 yük testindeki tablodan farklı.** O zaman Npgsql istemci havuzu (Max Pool
Size=100) tükeniyordu. Şimdi stack trace `NpgsqlConnector.RawOpen` / `ConnectAsync` — yani
**bağlantı kurulamıyor**. Bu istemci havuzu değil, **PgBouncer/PostgreSQL tarafı doygunluğu**
(`max_client_conn` veya `default_pool_size` kuyruğu). Ayrıca `already in a transaction` hatası
tamamen kaybolmuş.

### K5 — Outbox döngülerinin %33'ü DB yüzünden başarısız

`Error processing outbox messages` ×11, karşısında 22 başarılı lease. Stack trace'lerin hepsi
`Npgsql ... ConnectAsync` timeout. Worker, başkalarının yarattığı baskı yüzünden aç kalıyor.

---

## 4. Bulgular: Kod Seviyesinde

### B1 — [BUG] Süresi dolmuş lease hiçbir zaman geri alınmıyor

`NpgsqlOutboxLeaseStore` lease alırken `Status = Processing` yazıyor, ama aday sorgusu
`WHERE "Status" = @pending`. Yani `("LockedUntil" IS NULL OR "LockedUntil" < @now)` koşulu
**ölü kod**. Kod tabanında stale-lease reclaim eden bir bileşen (reaper) yok.

**Sonuç:** Worker lease ile outcome yazımı arasında çökerse (pod restart, HPA scale-down, OOM
— **veya K5'teki DB connect timeout'u**) satırlar kalıcı olarak `Processing`'de kalır ve
**hiç yayınlanmaz**. Dokümanın §29'da vaat ettiği crash recovery bugün çalışmıyor.

**`NpgsqlInboxLeaseStore`'da birebir aynı hata var.**

K5 bunu teorik olmaktan çıkarıyor: outbox döngüsü zaten düzenli olarak DB hatasıyla
kesiliyor.

Yan etki: bu satırlar `Processed` olmadığı için retention cleanup da silmiyor → kalıcı
birikim → index şişmesi.

### B2 — Cleanup her döngüde koşulsuz çalışıyor

`RunAsync` → `ProcessOutboxMessagesAsync` ardından **her seferinde**
`CleanupProcessedMessagesAsync`. 0 mesaj leaseleyen boş poll bile **iki ayrı `RequiresNew`
transactional UoW** açıyor. Inbox'ta `CleanupInterval: 01:00:00` config'i **var**, Outbox'ta
**yok**.

### B3 — Backoff, dolmayan batch'te de busy moda düşüyor

```csharp
delay = processed > 0 ? options.BusyPollingInterval : Min(delay * 2, options.MaxPollingInterval);
```

1 mesaj işlemek 100 ms'e sıfırlıyor → 10 poll / 102 sn tırmanma (K2'nin ana kaynağı).
Doğrusu: **batch dolduysa** busy, aksi hâlde `IdlePollingInterval`.

### B4 — Cleanup tracked-entity siliyor

`ToListAsync()` + `RemoveRange()` yerine `ExecuteDeleteAsync`.

### B5 — Dispatch index'i partial değil

`IX_OutboxMessages_Processing (Status, LockedUntil, NextRetryAt, CreatedAt)` — 7 günlük
retention ile `Processed` satırları da indeksleniyor. `LockedUntil`/`NextRetryAt` range
predicate olduğu için `ORDER BY CreatedAt` index sırasından karşılanamıyor → her poll'de sort.

---

## 5. Tasarımı Küçülten Ana Fikir

> **Dapr pub/sub'ın competing-consumer semantiği partition ownership'i bedavaya veriyor.**

Bir sinyal, app-id'nin **tam olarak bir** replica'sına teslim edilir. Dolayısıyla dokümanın
**§7.2'si (rendezvous hashing, stable membership, Kubernetes Lease API, worker registration
tablosu) tamamen gereksiz.**

Partition **yapılıyor** (§0), ama ownership makinesi yapılmıyor: sinyal partition'ı taşır,
Dapr onu tek bir pod'a teslim eder, o pod yalnızca o partition'ı leaseler. Ownership emergent
ve sinyal-başına olur; `SKIP LOCKED` correctness backstop olarak kalır. Bu, dokümanın kendi
ilkesinin ("Partition ownership performans optimizasyonudur. Row lease correctness
mekanizmasıdır.") sıfır koordinasyonla karşılanmış hâli.

---

## 6. Fazlar

### Faz 1 — Bug düzeltmesi + ucuz DB kazanımları

Yeni kavram yok, mimari risk yok. Faz 2'den bağımsız ship edilebilir.

**1.1 [BUG] Stale lease reclaim** — `NpgsqlOutboxLeaseStore` **ve** `NpgsqlInboxLeaseStore`:

```sql
WHERE ("Status" = @pending
       OR ("Status" = @processing AND "LockedUntil" IS NOT NULL AND "LockedUntil" < @now))
  AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= @now)
```

`RetryCount` bu yolda **artırılır** — aksi hâlde crash-loop'ta sonsuz yeniden lease olur;
`MaxRetryCount` sonrası dead-letter'a düşer. Duplicate publish üretebilir; at-least-once
sözleşmesi ve idempotent consumer Inbox'ı bunu zaten karşılıyor. Mevcut durumdan (kalıcı
kayıp) kesin olarak daha iyi.

**1.2 Backoff düzeltmesi** (B3) — `OutboxBackgroundService` + `InboxBackgroundService`:

```csharp
delay = processed >= options.BatchSize ? options.BusyPollingInterval
      : processed > 0                  ? options.IdlePollingInterval
      : Min(delay * 2, options.MaxPollingInterval);
```

Tek başına K2'deki 220 tırmanma poll'ünü sıfırlar (~%37 poll azalması).

**1.3 `CleanupInterval` + `CleanupBatchSize`** → `AetherOutboxOptions` (Inbox paritesi).
Boş döngü **2 tx → 1 tx**.

**1.4 Cleanup `ExecuteDeleteAsync`** kullanır.

**1.5 Partial index:**

```sql
CREATE INDEX CONCURRENTLY "IX_OutboxMessages_Dispatch"
ON sys_queues."OutboxMessages" ("PartitionId", "NextRetryAt", "CreatedAt")
INCLUDE ("LockedUntil")
WHERE "Status" IN (0, 1);
```

`Status IN (0,1)` — 1.1 sonrası `Processing` satırlarına da bakılacağı için. Index boyutu
tablo boyutundan bağımsız kalır. EF migration'ında raw SQL, `CONCURRENTLY`.

**`PartitionId` index'in ilk kolonu olarak Faz 1'de konur**, Faz 3'te değil. Faz 1'de hiçbir
sorgu onu filtrelemese de (öndeki eşitlik kolonu olmadan index yine `NextRetryAt`/`CreatedAt`
üzerinden taranır), böylece **sıcak tabloda ikinci bir index rebuild'i gerekmez**. Faz 3
yalnızca sorguyu değiştirir.

`Id` `INCLUDE`'a konmaz — primary key olduğu için Npgsql onu zaten leaf'te taşır.

**1.6 Retention** `Processed` için 7 gün → 1–2 gün (yalnızca config).

**1.7 `PartitionId` — yazma yolu (okuma yolu Faz 3'te)**

Kolon ve hash sözleşmesi **şimdi** ship edilir; tabloların boş/küçük olduğu an budur. IDM
hacmi geldikten sonra sıcak tabloda backfill yapmak kat kat pahalıdır (§0).

```sql
ALTER TABLE sys_queues."OutboxMessages"
  ADD COLUMN "PartitionId" smallint NOT NULL DEFAULT 0;
-- DEFAULT 0 sabit ifade → PG 11+ tablo rewrite yapmaz
ALTER TABLE sys_queues."OutboxMessages"
  ADD CONSTRAINT "CK_OutboxMessages_PartitionId"
  CHECK ("PartitionId" >= 0 AND "PartitionId" < 64);
```

`EfCoreOutboxStore.StoreAsync` içinde atanır:

```csharp
var partitionKey = envelope.Subject ?? envelope.Id;   // §1.8
PartitionId = (int)(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(partitionKey))
                    % (ulong)options.PartitionCount);
```

- `System.IO.Hashing.XxHash64` — `string.GetHashCode()` **kullanılmaz** (process'ler arası
  kararsız, doküman §6.3).
- `PartitionCount = 64`, tüm domain'ler için aynı. **Runtime knob değil** — değiştirmek
  migration + uyumluluk planı gerektirir. `AetherOutboxOptions` üzerinde bu şekilde belgelenir
  ve `partitionAlgorithm: "xxhash64-mod", partitionVersion: 1` metadata'sı yazılır.
- Faz 1'de bu kolon **hiçbir sorguda kullanılmaz** — yalnızca doldurulur. Davranış değişmez.

Aynı kolon `InboxMessages` için de eklenir (simetri; Inbox'ın kendi dispatcher'ı aynı
sorunları yaşıyor).

**1.8 Partition key seçimi — tek yönlü kapı**

`PartitionKey = envelope.Subject ?? envelope.Id`.

**`Subject` bugün zaten instance id ile dolu — ek iş gerekmiyor.** (Bu, spec'in önceki
sürümündeki "vNext `subject: null` geçiyor" tespitinin düzeltmesidir.)

Doğrulanan zincir:

```
vNext event kontratları  → [EventSubject] public required Guid InstanceId
                           (10 IDistributedEvent kontratının 10'unda da var — %100 kapsama)
DistributedEventBusBase  → subject ??= EventSubjectExtractor.ExtractSubject(payload)   (satır 42, 166)
DomainEventDispatcher    → subject: EventSubjectExtractor.ExtractSubject(@event)       (satır 42, 65, 108)
```

`TransitionEnqueueGateway.cs:52`'deki `subject: null` zararsız — base sınıf attribute'tan
dolduruyor.

**Tekillik kontrolü (§11-4'ün cevabı): `Subject`'in hiçbir yerde tekillik semantiği yok.**

- Aether `Subject`'i yalnızca `ExtraProperties["Subject"]`'e yazıyor
  (`EfCoreOutboxStore.cs:61`, `EfCoreInboxStore.cs:86`) — saf metadata passthrough.
- Index yok, unique constraint yok.
- **Inbox dedup `Subject`'e değil `Id`'ye bakıyor:**
  `AnyAsync(m => m.Id == eventId && m.Status == Processed)`. `CloudEventEnvelope.Id`
  envelope başına `Guid.NewGuid().ToString("N")`.
- Aether'ın kendi XML dokümantasyonu zaten böyle tanımlıyor: *"Optional subject identifier
  (e.g., aggregate ID)"* — yani kasıtlı olarak **tekil değil**, aggregate düzeyinde tekrar
  etmesi beklenen bir alan.

Dolayısıyla aynı instance'ın N event'inin aynı `Subject`'i taşıması tasarım gereği ve hiçbir
dedup/idempotency mekanizmasını bozmuyor. Partition key olarak kullanmak güvenli.

**Sonuç:** Aynı instance'ın tüm event'leri **1. günden itibaren** aynı partition'a düşer.
`?? envelope.Id` fallback'i `[EventSubject]` taşımayan (bugün yok, ileride eklenebilecek)
event'ler için güvenlik ağı olarak kalır.

Neden şimdi karar veriliyor: hash girdisini sonradan değiştirmek tüm mevcut satırların
partition'ını kaydırır. **Ordering garantisi verilmiyor** (bugün de yok); bu yalnızca ileride
istenirse kapıyı açık tutar.

**Beklenen etki:** Poll sayısı ~%37 azalır, poll başına tx yarıya iner → outbox döngüsü DB tx
**~1,6/sn → ~0,5/sn**. Crash sonrası kayıp mesajlar geri kazanılır. `PartitionId` doldurulmuş
olur; Faz 3 backfill gerektirmez.

---

### Faz 2 — Wake-up Signal

Latency'yi poll frekansından ayıran faz. Asıl kazanç, bunun **mümkün kıldığı** iki
konfigürasyon değişikliği (2.8).

**2.1 Kontrat** (`BBT.Aether.Abstractions`):

```csharp
public sealed record OutboxWakeupSignal(
    string RuntimeKey,
    int PartitionId,
    string? Source = null,
    DateTimeOffset? EmittedAt = null);

public interface IOutboxWakeupPublisher
{
    Task<bool> TryPublishAsync(OutboxWakeupSignal signal, CancellationToken cancellationToken);
}
```

`PartitionId` Faz 1.7'de hesaplanan gerçek değeri taşır. Faz 2'de worker bunu **yok sayar**
(hangi partition gelirse gelsin filtresiz leaseler); Faz 3'te anlam kazanır. Böylece Faz 3
sinyal kontratını değiştirmez ve iki faz bağımsız deploy edilebilir.

Doküman §27'deki `Source` alanı telemetri için — worker davranışı `Source`'a göre değişmez.

**2.2 Publisher** — `DaprOutboxWakeupPublisher`, best-effort: try/catch → warning + metrik,
`false` döner. **Business transaction'ı asla fail etmez.** `OperationCanceledException`
(caller iptali) yeniden fırlatılır. Sinyal TTL 30 sn (`ttlInSeconds`) — bayat sinyal
birikmesin.

**Sinyal outbox'ı BYPASS eder** — doğrudan `DaprClient.PublishEventAsync`, aksi hâlde
döngüsel olur.

**2.3 Coalescing** — `OutboxSignalCollector` (scoped). `EfCoreOutboxStore.StoreAsync`
`(runtimeKey, partitionId)` çiftini `HashSet`'e ekler; `IUnitOfWork.OnCompleted` commit
sonrası benzersiz çiftleri publish eder. Tek transaction'da 100 row = **1 sinyal**.

**2.4 Worker-side coordinator:**

```csharp
public interface IOutboxSignalCoordinator
{
    void Signal(string runtimeKey, int partitionId);
    Task<IReadOnlyCollection<OutboxSignalKey>> WaitAsync(TimeSpan timeout, CancellationToken ct);
}
```

`ConcurrentDictionary<OutboxSignalKey, byte>` (pending set) + `Channel.CreateBounded<bool>(1)`
`DropWrite` (uyandırma). Aynı partition için 10.000 sinyal → tek efektif kontrol.

**2.5 Subscription endpoint** — `[Topic("<pubsub>", "vnext-outbox-wakeup")]`, yalnızca
`coordinator.Signal(...)` + hemen `200`. DB sorgusu / publish / retry loop **yok**.
`PartitionId` range validation, aksi hâlde `400`.

**2.6 `OutboxBackgroundService`** `Task.Delay` yerine
`await coordinator.WaitAsync(options.FallbackInterval, stoppingToken)`.

**2.7 Flag'ler** — `SignalEnabled` (varsayılan `false`, kademeli açılır),
`FallbackPollingEnabled` **her zaman `true`**. Fallback polling correctness mekanizmasının
parçasıdır; hiçbir fazda kaldırılmaz. Sinyal yalnızca latency optimizasyonudur.

**2.8 Sinyal canlıya alındıktan sonra — asıl kazanç burada:**

```json
"MaxPollingInterval": "00:05:00"      // 60 sn → 300 sn
```

ve **Outbox/Inbox worker replica sayısı 10 → 2–3.** K1'e göre 0,073 msg/sn için 10 pod
gereksiz; sinyal, düşük pod sayısında bile latency'yi korur. Bu **doğrudan ~42 pod ve onların
tüm Npgsql havuzlarını topolojiden çıkarır** — outbox işinin PgBouncer'a en büyük katkısı
budur.

**Beklenen bileşik etki (Faz 1 + 2 + replica azaltma):**

```
Bugün : ~588 poll / 736 sn, ~1.176 tx  → 1,6 tx/sn, mesaj başına ~22 tx
Sonra : ~6 pod × 736/300 ≈ 15 poll + 22 sinyal-lease ≈ 37 tx → 0,05 tx/sn
                                                          ≈ 30× azalma
```

**2.9 Health** — sinyal çalışmıyorken worker **unready yapılmaz**; fallback poll ile devam
eder. Liveness = dispatcher loop yaşıyor mu; readiness = DB + broker; sinyal kesintisi
degraded/latency olayı olarak alarma bağlanır.

---

### Faz 3 — Partition okuma yolu

Yüksek hacim rejiminin (IDM peak) darboğazını çözen faz: 10 pod'un aynı index önekini
taraması. Faz 1.5 index'i, Faz 1.7 de kolonu zaten hazırladığı için burada **migration yok,
backfill yok, DDL yok** — yalnızca sorgu ve dispatcher değişikliği.

**3.1 Index** — **değişiklik yok.** `PartitionId` Faz 1.5'te zaten dispatch index'inin ilk
kolonu olarak konuldu; bu faz sıcak tabloya hiç DDL uygulamaz.

**3.2 Lease sorgusu** — `IOutboxLeaseStore.LeaseBatchAsync` opsiyonel partition listesi alır:

```sql
AND ("PartitionId" = ANY(@partitions) OR @partitions IS NULL)
```

`NULL` → filtresiz (fallback poll, mevcut davranış). Liste → yalnızca o partition'lar.

**3.3 Dispatcher davranışı**

```
Sinyal yolu   → yalnızca sinyallenen partition'lar leaselenir  → pod'lar ayrık index aralığı tarar
Fallback poll → partition filtresi YOK (tüm tablo)             → hiçbir partition aç kalmaz
PartitionId=-1 sinyali → fallback ile aynı (filtresiz)
```

Fallback'in filtresiz kalması kritik: sinyal kaybolan bir partition'ın reconciliation'ı buna
bağlı. Ownership tablosu / membership yok — Dapr competing-consumer dağıtımı yapıyor (§5).

**3.4 Ordering** — değişmiyor. Bugün cross-message ordering garantisi yok; partition eklemek
**regresyon yaratmıyor**. Doküman §14'teki "partition başına sequential publish" bilinçli
olarak yapılmıyor (§7) — yeni bir sözleşme vermek bu işin kapsamı değil.

**3.5 Aynı değişiklik Inbox dispatcher'ına** uygulanır.

**Faz 3 ne zaman devreye alınır:** Kod always-on ship edilir (flag yok — düşük hacimde
maliyeti yok, sinyal tek partition hedefler, fallback filtresizdir). Karşılığını IDM
hacminde verir.

**Tetikleyici metrik** (bu faza gerçekten ihtiyaç olduğunu doğrulayan): lease sorgusu p95
süresi replica sayısıyla birlikte artıyorsa çekişme var demektir.

---

## 7. Kapsam Dışı (bilinçli kararlar)

| Doküman bölümü | Karar | Gerekçe |
|---|---|---|
| §7.2 Rendezvous hashing / ownership / K8s Lease / worker registration | **Yapılmayacak** | Dapr competing-consumer bunu bedavaya veriyor (§5). Partition yapılıyor, ownership makinesi yapılmıyor |
| §15.1 `lease_version` fencing | **Ertelendi** | Mevcut `ExecuteUpdate ... WHERE LockedBy=@me AND LockedUntil>now` atomik. Metrik stale-completion gösterirse geri gelir |
| §17 Ayrı `outbox_dead_letters` tablosu | **Yapılmayacak** | Partial index (`Status IN (0,1)`) dead-letter'ı zaten dispatch index'inin dışında bırakıyor |
| §14 Partition başına sequential publish | **Yapılmayacak** | Bugün ordering garantisi yok; garanti vermek yeni sözleşme olur |
| §19 PostgreSQL table partitioning (`created_date`) | **Ertelendi** | Retention kısaltma (1.6) bu hacimlerde yeterli. **IDM tetikleyicisi:** `OutboxMessages` satır sayısı sürekli >10M kalırsa yeniden değerlendirilir. Logical partition ile karıştırılmamalı — o worker dağıtımı, bu storage/retention |
| §25–26 Drasi | **Şimdi iş yok** | Faz 2 sinyal kontratı zaten producer-agnostic; Drasi ileride `IOutboxWakeupPublisher`'ın yerine geçebilir, worker hiç değişmez |
| §20 HPA backlog metrikleri | **Ertelendi** | Önce replica sayısını düşür (2.8); HPA bu hacimde gereksiz |

---

## 8. Test Stratejisi

**Unit**
- Stale lease reclaim: `Processing` + dolmuş `LockedUntil` → yeniden leaselenir; geçerli
  `LockedUntil` → leaselenmez; `RetryCount` artar; `MaxRetryCount` sonrası dead-letter.
- Backoff: dolu batch → busy; kısmi batch → idle; boş → ×2 tavana kadar.
- Cleanup interval: süresi gelmemişse DB'ye hiç gitmez.
- Sinyal coalescing: aynı partition'a N row → 1 sinyal.
- Sinyal publish hatası → `CommitAsync` başarılı, exception sızmaz.
- Coordinator: aynı key için 10.000 `Signal` → tek `WaitAsync` sonucu.
- Partition (Faz 1.7): aynı key her zaman aynı partition; sonuç `0 <= p < 64`;
  `Subject` null ise `Id`'ye düşer; `Subject` dolu ise aynı `Subject`'li iki event aynı
  partition; dağılım 64 kova üzerinde makul dengeli (ki-kare veya basit min/max oranı).
- Partition (Faz 3): `@partitions = NULL` → filtresiz leaseler; liste verilince yalnızca o
  partition'lardan leaseler; `PartitionId = -1` sinyali filtresiz davranır.

**Integration**
- Commit sonrası sinyal → worker leaseledi.
- Rollback → sinyal publish edilmedi.
- Dapr erişilemez → request başarılı, fallback poll kaydı işledi.
- Worker lease aldı → publish öncesi crash → lease timeout → ikinci worker aldı
  (**B1 regresyon testi**).
- Duplicate sinyal ×10 → aynı mesaj concurrent leaselenmedi.

**Doğrulama (preprod, kod değil)**

Faz 0 taban çizgisi olarak §3'ün aynı Elastic sorguları tekrarlanır:

```sql
-- B1'in bugünkü şiddeti (Faz 1 öncesi/sonrası)
SELECT count(*) FROM sys_queues."OutboxMessages"
WHERE "Status" = 1 AND "LockedUntil" < now();
```

Başarı kriteri:
```
Stuck Processing count             : 0
"Error processing outbox messages" : Faz 1 öncesinin <%10'u
Outbox döngüsü DB tx/sn            : <0,1 (bugün ~1,6)
Yayınlanan mesaj başına tx         : <2 (bugün ~22)
P99 oldest pending age             : <5 sn
```

---

## 9. Riskler

| Risk | Azaltma |
|---|---|
| Aether SDK değişikliği diğer tüketicilere regresyon | Faz 1 davranış-koruyucu; Faz 2 `SignalEnabled` flag'i arkasında, varsayılan `false` |
| Stale lease reclaim duplicate publish üretir | At-least-once sözleşmesi zaten var; consumer Inbox idempotent. Kalıcı kayıptan iyidir |
| `MaxPollingInterval` 300 sn + replica 2–3 iken sinyal bozulursa latency patlar | Sinyal kesintisi degraded olarak alarma bağlanır (2.9); fallback her zaman aktif; kademeli rollout |
| Preprod'da `consumerID` pod-başına unique olabilir → her sinyal 10 pod'a gider | **Deploy repo'sundan doğrulanmalı (§10-3).** Unique ise `vnext-pubsub` (broadcast olmayan) component'i kullanıldığından emin ol |
| Replica azaltma throughput tavanını düşürür | K1 ölçülen domain'lerde tavanın çok altında; yine de kademeli (10 → 5 → 3) ve oldest-pending-age izlenerek. **IDM'e uygulanmaz** — replica azaltma domain başına karar, düşük hacimli domain'lerle sınırlı |
| Sinyal hacmi IDM peak'te Redis pubsub'ı yorar | Coalescing transaction başına (2.3) → sinyal/sn ≈ transaction/sn. TTL 30 sn bayat sinyali düşürür; `PartitionId = -1` üst sınırı çok-partition'lı transaction'ları tek sinyale indirir. Redis Streams bu mertebeyi kaldırır; yine de Faz 2 rollout'unda ölçülür |
| `PartitionCount = 64` sonradan yetersiz/fazla çıkar | 64, 10 pod için fazlasıyla yeterli (pod başına ~6 partition). Değiştirmek migration-sınıfı; `partitionVersion` metadata'sı ile versiyonlanır |

---

## 10. Bu Spec'in Kapsamı Dışındaki Asıl İş

K3 ve K4, pool sorununun bu tasarımla çözülmeyeceğini gösteriyor. Ayrı iş kalemleri olarak
açılmalı — **öncelik sırasıyla:**

1. **Npgsql `Max Pool Size` her connection string'de set edilmeli.** Bugün çıplak
   ([[load-test-remediation]]) → pod başına varsayılan 100. ~150 pod × 100 = teorik 15.000
   istemci bağlantısı, tek PgBouncer'a. Worker'lar için `Max Pool Size=5–10` fazlasıyla yeter
   (K1: 0,073 msg/sn). **Tek satırlık config, bu spec'in tamamından daha büyük etki.**
2. **PgBouncer `max_client_conn` / `default_pool_size` / `reserve_pool` ölçülmeli**
   (`SHOW POOLS`, `SHOW CLIENTS`). K4'teki `RawOpen` timeout'ları istemci havuzu değil,
   PgBouncer kuyruğuna işaret ediyor.
3. **Dapr `consumerID` deploy repo'sunda doğrulanmalı** (§9 riski).
4. **API pod'larındaki bağlantı churn'ü** — DB baskı imzalarının %94'ü orada. Ayrı analiz
   gerektirir; bu spec'in konusu değil.
5. Loglardaki DB-dışı hatalar (bu işle ilgisiz, ayrı issue): `Condition script ...
   'notificationType'` ×30, `script-task-kyc-cancel-application-...` compilation failed ×24,
   `[RENDER] API response missing renderId` ×9, `NotifyCourierEkycMernisSmsMapping ...
   'applicantInfo'` ×6.

---

## 11. Açık Sorular

1. Inbox tarafındaki aynı bug (B1) ve backoff düzeltmesi (B3) bu iş kaleminde mi düzeltilsin?
   **Öneri: evet, aynı PR** — birebir aynı düzeltme, ayırmak iki kez test yazmak demek.
2. Replica azaltma (2.8) bu spec'in parçası mı, ayrı ops iş kalemi mi? **Öneri: bu spec'te
   kal** — Faz 2'nin getirisinin çoğu buradan geliyor, ayrılırsa yapılmadan kalır.
3. Faz 1 tek başına (Faz 2 olmadan) preprod'a çıkarılsın mı? **Öneri: evet** — B1 aktif veri
   kaybı üretiyor, sinyal işini beklememeli.
4. ~~`envelope.Subject`'i instance id ile doldurmak ayrı bir vNext iş kalemi olsun mu?~~
   **KAPANDI (§1.8).** `Subject` zaten `[EventSubject]` ile instance id'den doluyor (10/10
   kontrat kapsaması) ve hiçbir yerde tekillik semantiği taşımıyor (inbox dedup `Id`'ye
   bakıyor). Ek vNext işi gerekmiyor; partition 1. günden itibaren instance bazlı gruplanır.
5. IDM domain'i için ölçüm: bu spec preprod'da doğrulanırken IDM benzeri bir yük profili
   (login/session hacmi) üretilebilir mi? Faz 3'ün tetikleyici metriği (§3.5 lease p95 vs
   replica sayısı) ancak o rejimde anlamlı ölçülür. **Öneri: Faz 3'ü ship et, tetikleyiciyi
   IDM canlıya çıkınca ölç** — kod always-on ve düşük hacimde maliyetsiz.
