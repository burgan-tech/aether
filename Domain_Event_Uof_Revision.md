Kesin — burada temel ilke şu olmalı:

“Uygulama akışında (request/command/handler) SaveChanges yalnızca UoW Commit aşamasında çağrılır.”
Store’lar (Inbox/Outbox) asla SaveChanges atmaz; sadece DbSet.Add/Update yapar.
Batch/process akışlarında (OutboxProcessor/Cleanup) ise kendi UoW’larını açıp sonunda tek bir SaveChanges/Commit yaparlar.

Böylece:
	•	Tutarlılık: Tek yazma noktası = UoW.
	•	Loop önleme: SaveChanges içinde tetiklenen publish/insert tekrar SaveChanges çağrısı yapmaz.
	•	Send-first/Outbox-first stratejileri UoW’nin içinde atomik şekilde uygulanır.

Aşağıya, mevcut dosyalarınızdaki değişiklik önerilerini net kurallar ve patch tarzı kodlarla veriyorum.

⸻

1) Kural Seti
	1.	Request/Command/Handler yolu
	•	EfCoreInboxStore, EfCoreOutboxStore SaveChanges çağırmaz.
	•	DistributedEventBusBase (veya DaprDistributedEventBus) send-first/outbox-first politikasını uygular; outbox’a yazarsa sadece DbSet.Add eder, flush etmez.
	•	DomainEventDispatcher yalnızca envelope üretir ve bus’a iletir; SaveChanges ile işi olmaz.
	•	AetherDbContext.SaveChanges(…) override içinde event/publish çağrısı yapmayın. Domain event toplama/publish UoW Commit pipeline’ında gerçekleşsin. (Detay aşağıda “Guard”)
	2.	Batch/Processor yolu (Hosted yok; manuel tetiklenen processor)
	•	OutboxProcessor, InboxCleanupService kendi UoW scope’larını açar veya “tek başına çalışırım” ise tek SaveChanges kullanır (batch sonunda).
	•	Batch içinde store’lar yine SaveChanges çağırmaz; processor çağırır (batch başına 1 kez).
	3.	Guard (loop önleme)
	•	AetherDbContext içinde bir guard bayrağı ile event toplama/publish’in SaveChanges sırasında tekrar tetiklenmesini engelleyin veya event/publish’i tamamen DbContext’ten çıkarıp UoW Commit’a alın.

⸻

2) Dosya bazlı düzeltmeler

Aşağıdaki patch örneklerinde amaç, store/dispatcher’lardan SaveChanges’i kaldırıp UoW Commit’e taşımak.

2.1 EfCoreOutboxStore.cs / EfCoreInboxStore.cs

Önce (problemli):

public async Task EnqueueAsync(OutboxMessage msg, CancellationToken ct)
{
    _db.Outbox.Add(msg);
    await _db.SaveChangesAsync(ct); // ❌ KALDIR
}

Sonra (doğru):

public async Task EnqueueAsync(OutboxMessage msg, CancellationToken ct)
{
    _db.Outbox.Add(msg); // sadece track et
    // SaveChanges yok; UoW commit sırasında flush edilecek
    await Task.CompletedTask;
}

Benzer şekilde InboxStore’da:

public async Task InsertAsync(InboxMessage msg, CancellationToken ct)
{
    _db.Inbox.Add(msg);
    await Task.CompletedTask; // SaveChanges yok
}

Not: TryDeduplicate senaryosunda da ExistsAsync + InsertAsync ikilisi UoW içinde yapılmalı; flush UoW’dadır.

⸻

2.2 DomainEventDispatcher.cs

Yapmaması gerekenler:
	•	SaveChanges çağırmak
	•	Outbox’a yazdıktan sonra flush etmek

Olması gereken:
	•	Aggregate’lardan toplanmış IDomainEvent → CloudEventEnvelope<T> üretmek → IDistributedEventBus.PublishAsync(envelope) çağırmak.
	•	Eğer send-first ve publish hatası gelirse: bus, outbox’a Add eder; SaveChanges yine yok.

Kısaca dispatcher “sadece publish intent” üretir.

⸻

2.3 DistributedEventBusBase.cs (veya Dapr implementasyonu)

Send-first: (öneri)

public async Task PublishAsync<T>(CloudEventEnvelope<T> env, CancellationToken ct)
{
    try
    {
        // Dapr publish
        await _dapr.PublishEventRawAsync(EventMeta<T>.PubSub, EventMeta<T>.Topic,
            _ser.Serialize(env), "application/cloudevents+json", ct);
    }
    catch (Exception ex)
    {
        // Outbox’a sadece ADD (no SaveChanges)
        _db.Outbox.Add(new OutboxMessage { /* fill from EventMeta<T> + env */ LastError = ex.Message, AttemptCount = 1 });
        // Flush yok; UoW commit’te
    }
}

Outbox-first: önce Add, sonra publish dener; yine SaveChanges yok, iş sonunda UoW commit.

⸻

2.4 OutboxProcessor.cs / InboxCleanupService.cs

Bunlar batch boundary olduğu için kendi Commit’ini yapabilir. İki iyi seçenek:

A) Kendi UoW scope’u:

public async Task<int> ProcessBatchAsync(int size, CancellationToken ct)
{
    await using var u = await _uow.BeginAsync(new() { IsTransactional = true }, ct);
    var batch = await _outbox.TakeBatchAsync(size, ct);

    foreach (var msg in batch)
    {
        try
        {
            await _dapr.PublishEventRawAsync(...);
            await _outbox.MarkSuccessAsync(msg, ct); // sadece state update
        }
        catch (Exception ex)
        {
            await _outbox.MarkFailureAsync(msg, ex.Message, ct);
        }
    }

    await u.CommitAsync(ct); // tek SaveChanges burada
    return batch.Count;
}

B) UoW kullanmayacaksanız: tek DbContext’le “batch başına 1 SaveChanges” çağırın, store’lar yine SaveChanges çağırmasın.

⸻

2.5 AetherDbContext.cs (loop guard)

Şu anda AetherDbContext içinde SaveChanges override’ınız tekrar outbox/inbox/publish tetikliyorsa loop/doğrulama zafiyeti yaratır. İki yaklaşım:

YAKLAŞIM-1: DbContext override’dan publish’i tamamen kaldırın
Event toplama ve publish zaten UoW Commit pipeline’ında yapılacak.

public override int SaveChanges(bool acceptAllChangesOnSuccess)
{
    // burada sadece base.SaveChanges çağırın
    return base.SaveChanges(acceptAllChangesOnSuccess);
}
public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    => base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

YAKLAŞIM-2: Guard ile tek geçiş (tercihen yine publish’i UoW’de tutun)

private static readonly AsyncLocal<bool> _isFlushing = new();
private bool IsFlushing { get => _isFlushing.Value; set => _isFlushing.Value = value; }

public override Task<int> SaveChangesAsync(bool accept, CancellationToken ct = default)
{
    if (IsFlushing) // re-entrancy guard
        return base.SaveChangesAsync(accept, ct);

    IsFlushing = true;
    try
    {
        // Publish/pipeline çağırmayın; yalnızca persist işi
        return base.SaveChangesAsync(accept, ct);
    }
    finally { IsFlushing = false; }
}

Tavsiyem: publish/collect işi DbContext’ten tamamen çıkarılsın, UoW Commit’te çalışsın. Böylece loop ihtimali yok olur.

⸻

3) UoW Commit sırası (önerilen)

Commit() içinde:
	1.	EF ChangeTracker → DomainEventCollector (aggregate’lardaki domain event’leri topla)
	2.	Outbox-first ise outbox kalemlerini ekle (flush YOK)
	3.	Send-first ise bus.Publish dener; hata alırsa outbox’a ekle (flush YOK)
	4.	DbContext.SaveChanges(…) 1 kez
	5.	(İsteğe bağlı) post-commit hook’lar (telemetry vs.)

Tek SaveChanges ile tüm state atomik. Publish’ın kendisi Dapr’a HTTP olduğu için DB transaction’a dahil değildir; send-first seçeneğinde “DB commit başarısız olup publish başarılı” edge-case’i vardır — bunu bilinçli seçiyoruz (opsiyon). Outbox-first ile bu tutarsızlık minimize edilir.

⸻

4) “Loop” nasıl oluşuyordu ve neden biter?
	•	Store’un içinde SaveChanges → AetherDbContext.SaveChanges override → tekrar event/publish/outbox akışına dokunuyorsa re-entrancy/loop oluşur.
	•	Store’lardaki SaveChanges’i kaldırıp (sadece Add/Update), publish/collect’i DbContext’ten çıkarıp UoW Commit’e aldığınızda tek flush kalır → loop yok.

⸻

5) Hızlı kontrol (repo’nuzdaki dosyalar)

Senin yolladığın dosyalarda SaveChanges çağrıları şuralarda gözüküyor (özet sayım):
	•	AetherDbContext.cs → 6 çağrı (override/baz/yardımcı) → publish’i buradan kaldırın
	•	EfCoreInboxStore.cs → 1 → kaldırın
	•	EfCoreOutboxStore.cs → 1 → kaldırın
	•	OutboxProcessor.cs → 2 → kalabilir (batch sonunda tek kez olacak şekilde düzenleyin)
	•	InboxCleanupService.cs → 1 → kalabilir (iş sınırı)
	•	CompositeUnitOfWork.cs / EfCoreTransactionSource.cs → SaveChanges yok (transaction yönetimi var)

Bu, yukarıdaki strateji ile bire bir örtüşüyor.

⸻

6) Örnek: UoW Commit parçası (tamamlama)

public async Task CommitAsync(CancellationToken ct = default)
{
    // 1) Domain events topla
    _db.CollectDomainEvents(_collector);

    // 2) Publish intent (send-first/outbox-first)
    var events = _collector.Drain();
    foreach (var e in events)
        await _domainEventDispatcher.DispatchAsync(e, ct); // Store içinde SaveChanges yok!

    // 3) Tek flush
    await _db.SaveChangesAsync(ct);
}

DispatchAsync içinde bus.Publish ve gerekirse outbox .Add edilir; flush edilmez.

⸻

7) Sonuç
	•	Tutarlılık: Request yolunda tek SaveChanges (UoW Commit).
	•	Loop önleme: DbContext override’ında publish/collect yok; store’lar SaveChanges atmaz.
	•	Processor’lar: Batch sınırında tek SaveChanges veya tek bir UoW.Commit.
	•	Politika: Send-first/Outbox-first UoW içinde uygulanır, store’lar yalın kalır.
